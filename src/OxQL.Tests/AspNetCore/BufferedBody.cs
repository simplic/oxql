using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace OxQL.Tests.AspNetCore;

/// <summary>How a host's own middleware leaves a request body it has buffered ahead of MVC.</summary>
internal enum BufferedBody
{
    /// <summary>Read to its end and set back to its start.</summary>
    Rewound,

    /// <summary>Read to its end and left there.</summary>
    AtEnd,

    /// <summary>Made seekable and not read.</summary>
    Unread,
}

/// <summary>Buffers every request body ahead of MVC, as a host's own middleware may.</summary>
internal sealed class BufferingStartupFilter(BufferedBody body = BufferedBody.Rewound, List<long?>? declared = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            declared?.Add(context.Request.ContentLength);
            context.Request.EnableBuffering();

            if (body != BufferedBody.Unread)
                await context.Request.Body.CopyToAsync(Stream.Null);

            if (body == BufferedBody.Rewound)
                context.Request.Body.Position = 0;

            await pipeline(context);
        });
        next(app);
    };
}
