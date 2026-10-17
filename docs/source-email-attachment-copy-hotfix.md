# Source email attachment copy hotfix

## Current confirmed defect

Review Source Email can only show attachment names unless the retained source evidence JSON contains `contentBase64` or `contentBytes` for each non-inline attachment.

The mailbox request contract already supports both fields through `MailboxAttachmentRequest.ContentBase64`, `MailboxAttachmentRequest.ContentBytes`, and `EffectiveContentBase64`.

## Required API change

Patch `Controllers/OrderIntakeController.cs` in `EnsureSourceEmailEvidence` so the retained `email-evidence` staged record stores attachment copies supplied by Power Automate:

```csharp
attachments = (request.Attachments ?? []).Select(attachment => new
{
    attachment.Name,
    attachment.ContentType,
    attachment.ContentId,
    attachment.Size,
    attachment.IsInline,
    contentBase64 = attachment.EffectiveContentBase64
}).ToList(),
```

Do not add attachment bytes to each order payload in `EnrichSourceEvidence`; keep order payloads metadata-only. The source-email endpoint already returns the retained `email-evidence` payload when available.

## Power Automate requirement

For each non-inline attachment, the flow must call **Get attachment content** and send either:

- `contentBase64`, or
- `contentBytes`

Attachment name/size/content type alone is not enough for Review Source Email to provide a downloadable copy.
