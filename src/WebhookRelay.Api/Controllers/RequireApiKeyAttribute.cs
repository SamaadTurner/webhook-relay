using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using WebhookRelay.Api.Options;

namespace WebhookRelay.Api.Controllers;

// Put this on a controller to require the admin key in the X-Api-Key header.
// The webhook endpoint doesn't use it because senders prove who they are with
// a signature instead.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        RelayOptions options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<RelayOptions>>().Value;

        string? provided = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();

        // If no key is configured, nobody gets in. That is safer than leaving
        // the admin endpoints open because someone forgot a setting.
        if (string.IsNullOrEmpty(options.AdminApiKey) || string.IsNullOrEmpty(provided))
        {
            context.Result = new UnauthorizedObjectResult(new { error = $"The {HeaderName} header is required." });
            return;
        }

        bool matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(options.AdminApiKey));

        if (!matches)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "API key is not valid." });
        }
    }
}
