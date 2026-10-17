#!/usr/bin/env python3
"""Turn failed tests in .trx result files into GitHub Actions error annotations.

Annotations appear on the pull request and in the check-run API, so a failing
test can be identified without downloading the raw job log.

Usage: annotate-test-failures.py <directory containing .trx files>
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_ANNOTATIONS = 10  # GitHub keeps at most 10 error annotations per step.
MAX_CHARS = 3500


def escape(value: str) -> str:
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def main() -> int:
    root_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    failures = []
    for path in glob.glob(os.path.join(root_dir, "**", "*.trx"), recursive=True):
        tree = ET.parse(path)
        for result in tree.getroot().iterfind(".//t:Results/t:UnitTestResult", NS):
            if result.get("outcome") != "Failed":
                continue
            name = result.get("testName", "unknown test")
            message = result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS).strip()
            stack = result.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS).strip()
            stdout = result.findtext("t:Output/t:StdOut", default="", namespaces=NS).strip()
            first_frame = next((line.strip() for line in stack.splitlines() if " in " in line), "")
            body = "\n".join(part for part in (message, first_frame, stdout and "Output:\n" + stdout) if part)
            failures.append((name, body[:MAX_CHARS]))

    for name, body in failures[:MAX_ANNOTATIONS]:
        print(f"::error title={escape(name)}::{escape(body)}")
    if len(failures) > MAX_ANNOTATIONS:
        print(f"::error title=More failed tests::{len(failures) - MAX_ANNOTATIONS} further failed tests are not shown.")
    print(f"{len(failures)} failed test(s) found in .trx results.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
