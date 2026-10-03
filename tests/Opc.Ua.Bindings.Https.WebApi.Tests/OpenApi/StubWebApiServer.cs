/* ========================================================================
 * Copyright (c) 2005-2026 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Opc.Ua.Bindings.Https.WebApi.Tests.OpenApi
{
    /// <summary>
    /// <see cref="IWebApiServer"/> that records the last request and
    /// answers with the response <see cref="Respond"/> builds, or an empty
    /// response of the route's type.
    /// </summary>
    internal sealed class StubWebApiServer : IWebApiServer
    {
        /// <summary>
        /// A stateless instance for tests that never inspect the
        /// recorded request.
        /// </summary>
        public static StubWebApiServer Shared { get; } = new();

        public StubWebApiServer()
            : this(ServiceMessageContext.CreateEmpty(new TestTelemetryContext()))
        {
        }

        public StubWebApiServer(IServiceMessageContext messageContext)
        {
            MessageContext = messageContext;
        }

        /// <summary>
        /// A context whose factory knows the built-in structures, so
        /// binary ExtensionObject bodies decode to their CLR type.
        /// </summary>
        public static IServiceMessageContext CreateFullContext()
        {
            return ServiceMessageContext.Create(new TestTelemetryContext());
        }

        public IServiceMessageContext MessageContext { get; }

        public bool IsReady => true;

        public IServiceRequest? LastRequest { get; private set; }

        public Func<IServiceRequest, IServiceResponse?>? Respond { get; set; }

        public ValueTask<IServiceResponse> InvokeAsync(
            IServiceRequest request,
            WebApiInvocationContext context,
            CancellationToken ct)
        {
            LastRequest = request;

            IServiceResponse? response = Respond?.Invoke(request);
            if (response == null)
            {
                if (!WebApiServiceRoutes.TryGetByRequestType(request.GetType(), out WebApiServiceRoute route))
                {
                    throw new InvalidOperationException($"No route for {request.GetType().Name}.");
                }
                response = (IServiceResponse)Activator.CreateInstance(route.ResponseType)!;
            }
            return new ValueTask<IServiceResponse>(response);
        }

        private sealed class TestTelemetryContext : TelemetryContextBase
        {
            public TestTelemetryContext()
                : base(NullLoggerFactory.Instance)
            {
            }
        }
    }
}
