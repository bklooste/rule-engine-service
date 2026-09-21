using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Templates;


internal sealed class BasePathTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.ApplicationServices.GetService<IHttpContextAccessor>();
        if (httpContext == null || httpContext.HttpContext == null)
            return Task.CompletedTask;
        var httpReq = httpContext.HttpContext.Request;
        var srv = httpReq.Headers["X-Forwarded-Host"];
        if (string.IsNullOrEmpty(srv))
            srv = httpReq.Host.Value;
        var scope = "https";
        if (srv.Any(x => x != null && x.Contains("localhost")))
            scope = "http";
        var url = $"{scope}://{srv}/{httpReq.Headers["X-Forwarded-Prefix"]}";
        (document.Servers ??= []).Add(new OpenApiServer { Url = url });
        return Task.CompletedTask;
    }
}





