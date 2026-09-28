using System.Net;
using ExecutorBalancer.Api.Security;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>
/// Главная страница с превью ссылки: мессенджеры (Telegram, WhatsApp, ВКонтакте) берут название, описание и картинку
/// из тегов Open Graph, а адрес картинки им нужен полный. Домен в репозитории не зашит: сервер подставляет адрес,
/// по которому страницу открыли (за Caddy — https и домен сайта), вместо %SITE_URL%. Если включён гостевой вход,
/// в описании говорится, что попробовать можно без пароля (%GUEST_NOTE%).
/// </summary>
public static class IndexPage
{
    private const string SiteUrl = "%SITE_URL%";
    private const string GuestNote = "%GUEST_NOTE%";

    public static IApplicationBuilder UseIndexPage(this IApplicationBuilder app, IWebHostEnvironment env)
    {
        var file = env.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists)
        {
            return app;
        }

        string template;
        using (var reader = new StreamReader(file.CreateReadStream()))
        {
            template = reader.ReadToEnd();
        }

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            if ((!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
                || request.Path.Value is not ("/" or "/index.html"))
            {
                await next(context);
                return;
            }

            // адрес экранируется: заголовок Host приходит от клиента и не должен ломать разметку
            var origin = WebUtility.HtmlEncode($"{request.Scheme}://{request.Host}");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            if (HttpMethods.IsGet(request.Method))
            {
                var guest = context.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.Guest is not null;
                var page = template.Replace(SiteUrl, origin, StringComparison.Ordinal)
                    .Replace(GuestNote, guest ? " Можно попробовать без пароля — вход гостем." : "", StringComparison.Ordinal);
                await context.Response.WriteAsync(page, context.RequestAborted);
            }
        });
    }
}
