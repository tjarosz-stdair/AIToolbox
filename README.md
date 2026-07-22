# AI Toolbox

ASP.NET Core MVC + Chrome extension platform for internal AI-assisted tools. The first tools are AI Compare and the PDF Product Parser, with shared admin, provider, directive, storage, access-control, job, and usage/cost infrastructure.

## Stack

- .NET 10 / ASP.NET Core MVC
- Microsoft 365 sign-in through OpenID Connect
- SQL Server via `Microsoft.Data.SqlClient`
- ASP.NET Core Data Protection for encrypting stored provider API keys
- Chrome Manifest V3 extension for extension-backed tools

## First-Time Setup

1. Create a SQL Server database or let the app create tables on startup using the configured connection string.
2. Keep `AIToolbox.Web/appsettings.json` as a placeholder. Put real values in ignored environment-specific configuration, IIS environment variables, user secrets, or a production secret store:
   - `ConnectionStrings:AITools`
   - `MicrosoftEntra:ClientId`
   - `MicrosoftEntra:ClientSecret`
   - `MicrosoftEntra:AllowedTenantIds`
   - `MicrosoftEntra:AllowedDomains`
   - `MicrosoftEntra:BootstrapAdminEmails`
   - `DataProtection:KeysPath`
3. Create a Microsoft Entra app registration:
   - Supported account types: any organizational directory, since Koch Air and Standard Air are separate tenants.
   - Redirect URI: `/signin-oidc` on each deployed host. If the app is mounted as an IIS application under `/aicompare`, the Entra redirect URI should be the full external URL `https://aitools.stdair.com/aicompare/signin-oidc`, while app configuration should keep `MicrosoftEntra:CallbackPath` as `/signin-oidc`.
   - Client secret: store outside source control, preferably user secrets, environment variables, or a production secret store.
4. Grant/admin-consent the app in both Microsoft 365 tenants.
5. Start the app once. `Database:InitializeOnStartup` creates the required tables and seeds bootstrap admins.
6. Sign in as a bootstrap admin, open the Admin Dashboard, add provider credentials, configure tools/directives/storage, then grant user/admin access.

## Authorization Model

Users must satisfy all checks:

- Sign in through Microsoft 365.
- Come from an allowed tenant ID.
- Use an allowed email domain.
- Have an enabled access grant in SQL Server, unless listed in `BootstrapAdminEmails`.

Admins can configure providers, user grants, tools, directives, model pricing, and storage settings. Basic users can use tools they are authorized for but cannot view or edit stored API credentials.

## Provider Types

- `OpenAiCompatible`: sends a chat-completions-style JSON payload with `Authorization: Bearer {key}`.
- `OpenAiResponses`: sends an OpenAI Responses API JSON payload with `Authorization: Bearer {key}`. Use this for OpenAI models such as `gpt-5.5` with `https://api.openai.com/v1/responses`.
- `AzureOpenAi`: sends a chat-completions-style JSON payload with `api-key: {key}`.
- `Anthropic`: sends a Messages-style JSON payload with `x-api-key: {key}` and `anthropic-version`.

Use the full provider endpoint URL in Provider Admin. The optional Extra Headers JSON field accepts a simple object such as:

```json
{
  "custom-header": "custom value"
}
```

Provider Admin includes an Export config button that copies configured provider metadata to the clipboard as JSON. API keys are never included in that export; it only reports whether a key is configured.

Prompt submission uses a streaming progress endpoint when JavaScript is available, so the page shows each selected provider as completed or failed and prevents duplicate submits while the run is active.

Saved AI Compare queries are stored as prompt runs with identity IDs. Users can reopen their private saved queries from History, view public saved queries from other authorized users, and reopen saved analysis runs from each response page. Existing databases are upgraded on startup with an `IsPublic` column on `dbo.PromptRuns`; existing rows default to private.

Tool execution jobs use shared `ToolJobs` metadata and tool-specific detail tables. Jobs can be private to the owner or public to other authenticated users who are authorized for the same tool. The first job type is PDF product extraction.

AI responses and analyses are rendered as Markdown using Markdig. Raw HTML is disabled and generated links are constrained to normal web, mail, anchor, or site-relative URLs.

Prompt, response, error, and analysis text are stored in `NVARCHAR(MAX)` columns and written with explicit `NVARCHAR(MAX)` SQL parameters. If a future response still ends abruptly, check the provider's output token limit or finish reason; `NVARCHAR(MAX)` itself is not limited to 4000 characters.

Provider configuration includes an optional Max output tokens setting. When providers return usage or stop metadata, saved response pages display finish reason plus input/output token counts to help diagnose incomplete answers.

## Production Notes

- Persist ASP.NET Core Data Protection keys somewhere stable across app restarts and instances; otherwise encrypted provider credentials may become unreadable after key rotation or redeployment. For IIS, create a folder such as `C:\ProgramData\AIToolbox\DataProtectionKeys`, grant the app pool identity read/write access, then set:

```json
"DataProtection": {
  "ApplicationName": "AIToolsCompare",
  "KeysPath": "C:\\ProgramData\\AIToolbox\\DataProtectionKeys"
}
```

- Keep `ClientSecret` and provider API keys out of source control.
- Use real tenant IDs instead of relying on domains alone.
- Consider adding audit exports and retention rules before storing sensitive prompts long term.

## Data Protection Troubleshooting

If a provider fails with `The key {guid} was not found in the key ring`, the app is trying to decrypt a stored provider API key with a Data Protection key that no longer exists. Configure `DataProtection:KeysPath`, recycle the app pool, then re-enter each affected provider API key in Provider Admin. Existing encrypted provider keys cannot be recovered after the old key ring is lost.

## IIS Auth Troubleshooting

If Microsoft sign-in appears to complete and then immediately loops back to `authorize`, check the `signin-oidc` response in browser developer tools:

- If there is no `.AIToolsCompare.Auth` `Set-Cookie` header, inspect the app logs for `MicrosoftEntra` warnings or `/Account/ExternalAuthFailed`.
- If the cookie is set but the next request does not send it back, confirm the app is reached through the same host/path (`https://aitools.stdair.com/aicompare`) and that browser privacy tooling is not blocking the cookie.
- If the user lands on `/Account/AccessDenied`, sign-in worked, but the user failed tenant/domain/user-grant checks. Confirm the signed-in email matches `BootstrapAdminEmails` or an enabled SQL access grant.
