using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Wedding.Api;

public static class OpenApiContract
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, context, ct) =>
        {
            document.Info.Title = "Wedding gifts API";
            document.Servers = [new OpenApiServer { Url = "/" }];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, ct) =>
        {
            var path = "/" + context.Description.RelativePath;
            var method = context.Description.HttpMethod;
            operation.Parameters ??= [];
            void Parameter(string name, ParameterLocation location, bool required, string type = "string", string? format = null)
            {
                operation.Parameters.Add(new OpenApiParameter { Name = name, In = location, Required = required,
                    Schema = new OpenApiSchema { Type = type == "integer" ? JsonSchemaType.Integer : JsonSchemaType.String, Format = format } });
            }
            if (path.StartsWith("/api", StringComparison.Ordinal))
            {
                if (path == "/api/webhooks/mercadopago")
                {
                    Parameter("x-signature", ParameterLocation.Header, true); Parameter("x-request-id", ParameterLocation.Header, true); Parameter("data.id", ParameterLocation.Query, true);
                    operation.RequestBody = new OpenApiRequestBody { Required = true, Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } } };
                }
                else
                {
                    if (method is "POST" or "PUT") Parameter("X-CSRF-Token", ParameterLocation.Header, true);
                    if (path.StartsWith("/api/contributions", StringComparison.Ordinal) && method == "POST") Parameter("Idempotency-Key", ParameterLocation.Header, true, format: "uuid");
                    if (path.StartsWith("/api/contributions/{", StringComparison.Ordinal)) Parameter("X-Contribution-Token", ParameterLocation.Header, true);
                    if (path.StartsWith("/api/admin", StringComparison.Ordinal) || path is "/api/auth/me" or "/api/auth/logout") Parameter("Wedding.Session", ParameterLocation.Cookie, true);
                    if (path == "/api/admin/contributions")
                    {
                        Parameter("page", ParameterLocation.Query, false, "integer"); Parameter("pageSize", ParameterLocation.Query, false, "integer"); Parameter("status", ParameterLocation.Query, false);
                    }
                }
                operation.Responses ??= new OpenApiResponses();
                foreach (var status in new[] { "400", "401", "403", "404", "409", "422", "429", "502", "503" })
                    operation.Responses.TryAdd(status, new OpenApiResponse { Description = "ProblemDetails; see spec.md for endpoint-specific conditions.", Content = new Dictionary<string, OpenApiMediaType>
                    { ["application/problem+json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } } });
            }
            return Task.CompletedTask;
        });
    }
}
