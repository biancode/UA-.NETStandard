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
using Opc.Ua.Client;
using Opc.Ua.Machinery.Result;

namespace Opc.Ua.Machinery.Client
{
    /// <summary>
    /// Teaches a session the structured types the OPC 40001 series defines, so
    /// a method output such as <c>GetLatestResult</c>'s <c>ResultDataType</c>
    /// decodes into the generated type instead of staying an opaque
    /// <see cref="ExtensionObject"/>.
    /// </summary>
    internal static class MachineryEncodeableRegistration
    {
        public static void Register(ISession session)
        {
            IServiceMessageContext messageContext = session.MessageContext ??
                throw new ArgumentException(
                    "The session must provide a message context.",
                    nameof(session));

            IEncodeableFactory factory = messageContext.Factory;
            IEncodeableFactoryBuilder builder = factory.Builder;
            bool commit = false;

            // OPC 40001-101 is the only part of the series with structured
            // DataTypes of its own; the rest reach the wire as built-in types
            // or through the models they compose.
            if (!factory.ContainsEncodeableType(
                Opc.Ua.Machinery.Result.DataTypeIds.ResultDataType))
            {
                builder = builder.AddOpcUaMachineryResult();
                commit = true;
            }

            if (commit)
            {
                builder.Commit();
            }
        }
    }
}
