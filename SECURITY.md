# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in CCProxy, please report it through [GitHub Security Advisories](https://github.com/worndown/ccproxy/security/advisories/new).

Do **not** open a public issue for security vulnerabilities.

## Scope

This policy covers the CCProxy proxy code itself. Issues with upstream services (OpenAI, Azure OpenAI) should be reported to those providers directly.

## Security Considerations

**Log files contain sensitive data.** When `--logfile` is specified, CCProxy writes the full content of all requests and responses to disk — including conversation history, tool calls, and any data passed to the model. This may include PII or other sensitive information. Keep log files secure, restrict access, and delete them when no longer needed.
