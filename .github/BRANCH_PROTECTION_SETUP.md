# GitHub Branch Protection Setup for Production

**Status:** Manual setup required  
**Reason:** GitHub API does not expose safe branch protection write operations

---

## Required Access

- Repository owner or admin with `admin` permission on the repository
- Estimated time: 5-10 minutes

---

## Step 1: Navigate to Branch Protection Settings

1. Go to https://github.com/Stuart-Lyons-Haulage/slh-tms-api
2. Click **Settings** (top right)
3. In left sidebar, click **Branches**
4. Click **Add rule** button under "Branch protection rules"

---

## Step 2: Create Rule for `main` Branch

In the "Branch name pattern" field, enter:
```
main
```

---

## Step 3: Configure Required Protections

### ✅ Require a pull request before merging
- [x] Require pull request reviews before merging
- [ ] Require status checks to pass before merging (we'll enable this next)
- [x] Require conversation resolution before merging
- [x] Require signed commits
- [ ] Require deployments to succeed before merging (not applicable yet)

**Required number of approvals:** 2
- [x] Dismiss stale pull request approvals when new commits are pushed
- [x] Require review from code owners

---

## Step 4: Require Status Checks to Pass

Check the box:
- [x] **Require status checks to pass before merging**
- [x] **Require branches to be up to date before merging**

Then select these status checks (search and add each):

```
✓ build (API CI workflow)
✓ test (xUnit tests)
✓ codeql (CodeQL security analysis)
✓ scheduled-jobs-build (Scheduled job tasks)
✓ power-automate-validation-info-mailbox (Info mailbox intake)
✓ power-automate-validation-dispatch (Load plan dispatch)
```

**Note:** These status checks are created automatically by GitHub Actions workflows. If they don't appear in the dropdown:

1. Ensure at least one commit has been pushed with these workflows defined in `.github/workflows/`
2. Run the workflows manually once so GitHub registers them
3. Then return to this settings page and add them

---

## Step 5: Restrict Who Can Push

Under **Restrict who can push to matching branches**:

- [x] **Restrict who can push to matching branches**

Select users/teams who can bypass these rules:
  - [ ] (Leave empty – no direct push bypass)
  
Alternatively, if emergency access is needed, add only:
  - Team: `@Stuart-Lyons-Haulage/admins` (if one exists)
  - Or specific user accounts

**Important:** This prevents accidental direct pushes and ensures all code goes through PR review.

---

## Step 6: Prevent Deletion & Force Push

- [x] **Allow force pushes**
  - Select: **Only administrators**
  - (Allows disaster recovery without disabling the rule entirely)

- [x] **Allow deletions**
  - Select: **Only administrators**

- [x] **Block deletions**
  - (Alternative: if no admin should delete, leave unchecked)

---

## Step 7: Save the Rule

Scroll to bottom and click the green **Create** button.

---

## Verification

After setup, verify the rule is active:

1. Go to Settings → Branches
2. You should see a rule card for `main` with all the protection icons
3. Try creating a test PR:
   - Push a test branch with a small change
   - Create a PR
   - Verify you **cannot** merge without:
     - 2 approvals
     - All status checks passing (green)
     - Conversation resolved
   - Verify direct push to `main` is blocked

---

## Testing Branch Protection

### Test 1: Require Approvals
```bash
git checkout -b test/branch-protection
echo "test" > test.txt
git add test.txt
git commit -m "Test commit"
git push origin test/branch-protection

# Go to GitHub and create a PR
# Attempt to merge without approval – should be blocked ✓
```

### Test 2: Require Status Checks
Same test as above, but with approvals granted:
- Merge button should remain disabled until all workflows pass ✓

### Test 3: Direct Push Blocked
```bash
git checkout main
git pull origin main
echo "test" > test.txt
git add test.txt
git commit -m "Direct commit"
git push origin main

# Should fail with:
# remote: error: GH006: Protected branch update failed for refs/heads/main.
# remote: error: At least 2 approving reviews are required by reviewers with
# remote: error: write access.
```
✓ Expected – rule is working!

---

## Post-Setup: Update Deployment Workflow

The GitHub Actions deployment workflow should now **only run on `main`** and should not need special bypass logic:

```yaml
on:
  push:
    branches:
      - main  # Only deploy from main (which requires PR)
```

This is already configured in `.github/workflows/deploy.yml`.

---

## Troubleshooting

### Workflows Not Appearing in Status Check Dropdown

**Problem:** Can't find the workflow checkboxes

**Solution:**
1. Ensure workflows are defined in `.github/workflows/*.yml`
2. Push to a test branch and create a PR
3. Wait 2-3 minutes for workflows to execute
4. Return to branch protection settings
5. Status checks should now be discoverable

### Need to Bypass for Emergency

If an emergency requires direct push to `main`:

1. Temporarily remove the branch protection rule
2. Make the emergency fix
3. **Immediately re-enable** the protection
4. Create a post-incident review

**Better alternative:** Use a temporary bypass rule (Step 6 above) with admin-only force push.

---

## Removing or Modifying the Rule

If the rule needs changes later:

1. Go to Settings → Branches
2. Click the `main` rule card
3. Click **Edit** (pencil icon)
4. Make changes and click **Save changes**

Or:

1. Click **Delete** to remove entirely (not recommended)
2. Create a new rule with updated settings

---

## Reference

- [GitHub Branch Protection Documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/managing-a-branch-protection-rule)
- [About Required Status Checks](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches#require-status-checks-before-merging)
