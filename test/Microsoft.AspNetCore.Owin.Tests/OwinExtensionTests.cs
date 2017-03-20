// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.AspNetCore.Owin
{
    using AddMiddleware = Action<Func<
          Func<IDictionary<string, object>, Task>,
          Func<IDictionary<string, object>, Task>
        >>;
    using AppFunc = Func<IDictionary<string, object>, Task>;
    using CreateMiddleware = Func<
          Func<IDictionary<string, object>, Task>,
          Func<IDictionary<string, object>, Task>
        >;
    using WebSocketAccept =
        Action
        <
            IDictionary<string, object>, // WebSocket Accept parameters
            Func // WebSocketFunc callback
            <
                IDictionary<string, object>, // WebSocket environment
                Task // Complete
            >
        >;
    using WebSocketAcceptAlt =
        Func
        <
            WebSocketAcceptContext, // WebSocket Accept parameters
            Task<WebSocket>
        >;

    public class OwinExtensionTests
    {
        static AppFunc notFound = env => new Task(() => { env["owin.ResponseStatusCode"] = 404; });

        [Fact]
        public async Task OwinConfigureServiceProviderAddsServices()
        {
            var list = new List<CreateMiddleware>();
            AddMiddleware build = list.Add;
            IServiceProvider serviceProvider = null;
            FakeService fakeService = null;

            var builder = build.UseBuilder(applicationBuilder =>
            {
                serviceProvider = applicationBuilder.ApplicationServices;
                applicationBuilder.Run(context =>
                {
                    fakeService = context.RequestServices.GetService<FakeService>();
                    return Task.FromResult(0);
                });
            },
            new ServiceCollection().AddSingleton(new FakeService()).BuildServiceProvider());

            list.Reverse();
            await list
                .Aggregate(notFound, (next, middleware) => middleware(next))
                .Invoke(new Dictionary<string, object>());

            Assert.NotNull(serviceProvider);
            Assert.NotNull(serviceProvider.GetService<FakeService>());
            Assert.NotNull(fakeService);
        }

        [Fact]
        public async Task OwinDefaultNoServices()
        {
            var list = new List<CreateMiddleware>();
            AddMiddleware build = list.Add;
            IServiceProvider expectedServiceProvider = new ServiceCollection().BuildServiceProvider();
            IServiceProvider serviceProvider = null;
            FakeService fakeService = null;
            bool builderExecuted = false;
            bool applicationExecuted = false;

            var builder = build.UseBuilder(applicationBuilder =>
            {
                builderExecuted = true;
                serviceProvider = applicationBuilder.ApplicationServices;
                applicationBuilder.Run(context =>
                {
                    applicationExecuted = true;
                    fakeService = context.RequestServices.GetService<FakeService>();
                    return Task.FromResult(0);
                });
            },
            expectedServiceProvider);

            list.Reverse();
            await list
                .Aggregate(notFound, (next, middleware) => middleware(next))
                .Invoke(new Dictionary<string, object>());

            Assert.True(builderExecuted);
            Assert.Equal(expectedServiceProvider, serviceProvider);
            Assert.True(applicationExecuted);
            Assert.Null(fakeService);
        }

        [Fact]
        public async Task OwinDefaultNullServiceProvider()
        {
            var list = new List<CreateMiddleware>();
            AddMiddleware build = list.Add;
            IServiceProvider serviceProvider = null;
            FakeService fakeService = null;
            bool builderExecuted = false;
            bool applicationExecuted = false;

            var builder = build.UseBuilder(applicationBuilder =>
            {
                builderExecuted = true;
                serviceProvider = applicationBuilder.ApplicationServices;
                applicationBuilder.Run(context =>
                {
                    applicationExecuted = true;
                    fakeService = context.RequestServices.GetService<FakeService>();
                    return Task.FromResult(0);
                });
            });

            list.Reverse();
            await list
                .Aggregate(notFound, (next, middleware) => middleware(next))
                .Invoke(new Dictionary<string, object>());

            Assert.True(builderExecuted);
            Assert.NotNull(serviceProvider);
            Assert.True(applicationExecuted);
            Assert.Null(fakeService);
        }

        [Fact]
        public async Task UseOwin()
        {
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var builder = new ApplicationBuilder(serviceProvider);
            IDictionary<string, object> environment = null;
            var context = new DefaultHttpContext();

            builder.UseOwin(addToPipeline =>
            {
                addToPipeline(next =>
                {
                    Assert.NotNull(next);
                    return async env =>
                    {
                        environment = env;
                        await next(env);
                    };
                });
            });
            await builder.Build().Invoke(context);

            // Dictionary contains context but does not contain "websocket.Accept" or "websocket.AcceptAlt" keys.
            Assert.NotNull(environment);
            var value = Assert.Single(
                    environment,
                    kvp => string.Equals(typeof(HttpContext).FullName, kvp.Key, StringComparison.Ordinal))
                .Value;
            Assert.Equal(context, value);
            Assert.False(environment.ContainsKey("websocket.Accept"));
            Assert.False(environment.ContainsKey("websocket.AcceptAlt"));
        }

        [Fact]
        public async Task UseOwinEx()
        {
            const string TestStringKey = "TestString";
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var builder = new ApplicationBuilder(serviceProvider);
            var context = new DefaultHttpContext();
            context.Items[TestStringKey] = string.Empty;

            WebSocketAcceptAlt webSocketAcceptAlt = webSocketAcceptContext => Task.FromResult((WebSocket)new FakeWebSocket());
            context.Items["websocket.AcceptAlt"] = webSocketAcceptAlt;
            bool isWebSocketCallbackInvoked = false;

            builder.UseOwinEx(addToPipeline =>
            {
                addToPipeline((env, next) =>
                {
                    Assert.NotNull(next);
                    var testString = (string)env[TestStringKey];
                    Assert.Equal(string.Empty, testString);
                    env[TestStringKey] = testString + "0";
                    return next();
                });
                addToPipeline(async (env, next) =>
                {
                    Assert.NotNull(next);

                    var testString = (string)env[TestStringKey];
                    Assert.Equal("0", testString);
                    env[TestStringKey] = testString + "1";

                    await next();

                    testString = (string)env[TestStringKey];
                    Assert.Equal("012", testString);
                    env[TestStringKey] = testString + "b";
                });
                addToPipeline(async (env, next) =>
                {
                    WebSocketAccept webSocketAccept = (WebSocketAccept)env["websocket.Accept"];
                    webSocketAccept(null, env2 =>
                    {
                        isWebSocketCallbackInvoked = true;
                        return Task.FromResult(0);
                    });
                    await next();
                    env["owin.ResponseStatusCode"] = 101;
                });
            });

            builder.Use((ctx, next) =>
            {
                Assert.NotNull(next);
                var testString = (string)ctx.Items[TestStringKey];
                Assert.Equal("01", testString);
                ctx.Items[TestStringKey] = testString + "2";
                return next();
            });

            var app = builder.Build();
            await app(context);

            Assert.Equal("012b", (string)context.Items[TestStringKey]);
            Assert.True(isWebSocketCallbackInvoked);
        }

        private class FakeService
        {
        }

        private class FakeWebSocket : WebSocket
        {
            public override WebSocketCloseStatus? CloseStatus => throw new NotImplementedException();

            public override string CloseStatusDescription => throw new NotImplementedException();

            public override string SubProtocol => throw new NotImplementedException();

            public override WebSocketState State => WebSocketState.Closed;

            public override void Abort()
            {
                throw new NotImplementedException();
            }

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public override void Dispose()
            {
                throw new NotImplementedException();
            }

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }
    }
}
