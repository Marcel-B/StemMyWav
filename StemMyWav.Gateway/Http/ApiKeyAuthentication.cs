using System.Security.Cryptography;
using System.Text;

namespace StemMyWav.Gateway.Http;

public static class ApiKeyAuthentication
{
    /// <summary>Verlangt den Header X-Api-Key für alles unterhalb von /api. Health, OpenAPI und
    /// Swagger UI bleiben offen, damit sie ohne Schlüsselwert geprüft werden können.</summary>
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app, string expected) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") &&
                !Matches(context.Request.Headers["X-Api-Key"].ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

    /// <summary>Vergleicht über die Hashes, damit die Laufzeit nichts über den Schlüssel verrät.</summary>
    private static bool Matches(string supplied, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
