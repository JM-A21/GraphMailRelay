# GraphMailRelay
A basic .NET-based background worker service for Windows intended to act as a simple and to-the-point SMTP relay for applications, services, and devices that don't natively support the Graph API, allowing these devices to still send mail in a modern and secure way.

## Requirements

- Office 365 tenant. Only the Global and US Government L4 (aka GCC High) environments are currently supported.
- [.NET Desktop Runtime 7.x](https://dotnet.microsoft.com/en-us/download/dotnet/7.0)

## Overview

The application is structured into two parts: the [SmtpServer](https://github.com/cosullivan/SmtpServer) for receiving SMTP messages, and a Graph API client built using Microsoft's Graph SDK. Each component is configured by a separate section of the settings file as described in the next section.

At this time, the SmtpServer component of this relay service is only set up for receiving plain and unencrypted SMTP messages on a user-configurable port. Although this is part of the design intent, care should be taken to ensure that the unencrypted traffic being received from the relay is protected from malicious intent. Ideally, the relay should be installed on the source system that is sending outgoing mail.

## Office 365 / Azure Configuration ##

> **Note**
> This section of the README is under construction, but in short, an App Registration needs to be created in Azure Active Directory with the `Mail.Send` permission for the Microsoft Graph API. A client secret must then be created for the app registration which is then provided in the relay's configuration file in the `AzureClientSecret` setting. Other methods of authentication are not currently supported.

> **Warning**
> Although the Graph API `Mail.Send` should function properly as a user-level permission configured for the appropriate account, the app has only be tested using an Application-level permission with admin consent granted for the organization. However, [application access policies](https://learn.microsoft.com/en-us/graph/auth-limit-mailbox-access) may be added separately by administrators to restrict the app registration in question to specific mailboxes.

## Local Relay Configuration (appsettings.json)

> **Warning**
> The configuration file contains sensitive client access secrets, is removed on uninstall, and is overwritten on upgrade. Ensure all data used in your configuration file is backed up to a secure location.

The configuration file for the service is named `appsettings.json`. When built from code, it will be present in the output directory of the built executable. When installed using the provided MSI installer, it will be located in `%PROGRAMDATA%\JM-A21\GraphMailRelay`. Starting the application while some of these settings are null or missing will result in the application writing errors to the console and the Windows `Application` event log before shutting down.

```json
{
	"Logging": {
		"LogLevel": {
			"Default": "Information",
			"Microsoft.Hosting.Lifetime": "Information"
		}
	},

	"EventLog": {
		"LogLevel": {
			"Default": "Information",
			"Microsoft.Hosting.Lifetime": "Warning"
		}
	},

	"SmtpConfiguration": {
		"ServerName": "localhost",
		"ServerPort": 25,
		"AllowedSenderAddresses": [
			"127.0.0.1",
			"localhost"
		]
	},

	"GraphConfiguration": {
		"AzureTenantId": "00000000-0000-0000-0000-000000000000",
		"AzureClientId": "00000000-0000-0000-0000-000000000000",
		"AzureClientSecret": "rWe0V3DOjSeHr0GRonWE_FakeSecret_RMpXkZaHVBSYRjhqdmGi",
		"AzureMailUser": "relayagent@contoso.com",
		"EnvironmentName": "GraphGlobal",

		"HttpResponseCapture": false
	}
}
```
### SmtpConfiguration

This section of the file configures the SmtpServer component of the relay application. The default settings in this section are sufficient for receiving mail from a local application or service via either `localhost` or `127.0.0.1` on port `25`, but the settings may be modified per your needs as described below.

- **ServerName**: The name the SmtpServer will run under.
    - Mostly intended for use later once the relay supports receiving encrypted mail.
- **ServerPort**: The port the SmtpServer will listen on. 
    - Note: If the relay will be receiving mail from anywhere other than the local server, ensure that your chosen port is open in Windows Firewall.
- **AllowedSenderAddresses**: A list of addresses and/or DNS names for endpoints the relay will accept mail from.
    - Note: If the relay receives mail from an endpoint outside of this list, the relay will drop the message and write a warning to the logging system.

### GraphConfiguration

This section of the file configures the Graph API client component of the relay application. Most settings in this section are defaulted as `null` on a fresh install and must be configured for the relay to operate.

> **Note**
> All GUIDs referenced below must be in format "00000000-0000-0000-0000-000000000000" (no `{}` curly braces) and are not case-sensitive.

> **Note**
> All location notes below are as of 2023-03-23 in the Office 365 Global environment and may not remain accurate in the future.

- **AzureTenantId**: Tenant identifier GUID.
    - Can be found in Azure Active Directory on the "Overview" page
- **AzureClientId**: Application (also known as Client) identifier GUID
    - Can be found in Azure Active Directory on either the Enterprise Application or App Registration that will be used, on the "Overview" page
- **AzureClientSecret**: Client secret string.
    - Can be found/created in Azure Active Directory on the App Registration that will be used, on the "Certificates & Secrets" page.
- **AzureMailUser**: The GUID or User Principal Name of the user that will be used for sending mail via Graph.
- **EnvironmentName**: The Azure / Graph environment to use. May be one of the following values:
    - `"GraphGlobal"` for standard / commercial Office 365 tenants.
    - `"GraphUSGovL4"` for US Government L4 (also known as GCC High).
- **HttpResponseCapture**: Whether to enable logging of Graph API response content to console. Used to diagnose request failures. May be `true` or `false`.
