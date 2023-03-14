﻿using JMS.Dtos;
using JMS.Domains;
using System;
using System.Collections.Generic;
using System.Text;
using Way.Lib;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using Org.BouncyCastle.Ocsp;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace JMS.Applications
{
    class HttpHandler : IRequestHandler
    {
        MicroServiceHost _MicroServiceProvider;
        ControllerFactory _controllerFactory;
        ILogger<HttpHandler> _logger;
        public InvokeType MatchType => InvokeType.Http;

        static MethodInfo PingMethod;
        static object[] PingMethodParameters;
        public HttpHandler(ControllerFactory controllerFactory, MicroServiceHost microServiceProvider)
        {
            this._MicroServiceProvider = microServiceProvider;
            this._controllerFactory = controllerFactory;
            this._logger = microServiceProvider.ServiceProvider.GetService<ILogger<HttpHandler>>();
        }

        /// <summary>
        /// 响应串
        /// </summary>
        public string GetResponse(IDictionary<string,string> header)
        {
                string secWebSocketKey = header["Sec-WebSocket-Key"].ToString();
                string m_Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                string responseKey = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(secWebSocketKey + m_Magic)));

                StringBuilder response = new StringBuilder(); //响应串
                response.Append("HTTP/1.1 101 Web Socket Protocol JMS\r\n");

                //将请求串的键值转换为对应的响应串的键值并添加到响应串
                response.AppendFormat("Upgrade: {0}\r\n", header["Upgrade"]);
                response.AppendFormat("Connection: {0}\r\n", header["Connection"]);
                response.AppendFormat("Sec-WebSocket-Accept: {0}\r\n", responseKey);
                if (header.ContainsKey("Origin"))
                {
                    response.AppendFormat("WebSocket-Origin: {0}\r\n", header["Origin"]);
                }
                if (header.ContainsKey("Host"))
                {
                    response.AppendFormat("WebSocket-Location: {0}\r\n", header["Host"]);
                }

                response.Append("\r\n");

                return response.ToString();
            
        }

        public async Task Handle(NetClient netclient, InvokeCommand cmd)
        {
            cmd.Header = new Dictionary<string, string>();
            var urlLine = await ReadHeaders(cmd.Service, netclient, cmd.Header);

            

            string subProtocol = null;
            cmd.Header.TryGetValue("Sec-WebSocket-Protocol", out subProtocol);//[Connection, Upgrade] //Upgrade, websocket

            using (var websocket = WebSocket.CreateFromStream(netclient.InnerStream, true, subProtocol, Timeout.InfiniteTimeSpan))
            {

                var path = urlLine.Split(' ')[1];
                if (path.StartsWith("/") == false)
                    path = "/" + path;

                var route = path.Split('/');
                cmd.Service = route[1];
                if (cmd.Service.Contains("?"))
                {
                    cmd.Service = cmd.Service.Substring(0, cmd.Service.IndexOf("?"));
                }
                var controllerTypeInfo = _controllerFactory.GetControllerType(cmd.Service);

                object userContent = null;
                if (controllerTypeInfo.NeedAuthorize)
                {
                    var auth = _MicroServiceProvider.ServiceProvider.GetService<IAuthenticationHandler>();
                    if (auth != null)
                    {
                        try
                        {
                            userContent = auth.Authenticate(cmd.Header);
                        }
                        catch
                        {
                            var data = System.Text.Encoding.UTF8.GetBytes($"HTTP/1.1 401 NotAllow\r\nAccess-Control-Allow-Origin: *\r\n\r\n");
                            netclient.Write(data);
                            Thread.Sleep(2000);
                            return;
                        }
                    }
                }


                try
                {
                    var responseText = GetResponse(cmd.Header);
                    netclient.InnerStream.Write(Encoding.UTF8.GetBytes(responseText));
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    netclient.ReadTimeout = 0;
                    using (IServiceScope serviceScope = _MicroServiceProvider.ServiceProvider.CreateScope())
                    {
                        MicroServiceControllerBase.RequestingObject.Value =
                            new MicroServiceControllerBase.LocalObject(netclient.RemoteEndPoint, cmd, serviceScope.ServiceProvider, userContent, path);

                        var controller = (WebSocketController)_controllerFactory.CreateController(serviceScope, controllerTypeInfo);
                        
                        keepAlive(websocket);

                        await controller.OnConnected(websocket);
                    }

                    if (websocket.State == WebSocketState.Open || websocket.State == WebSocketState.CloseReceived)
                    {
                        await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    await Task.Delay(2000);
                }
                catch (Exception)
                {
                    if (websocket.State == WebSocketState.Open || websocket.State == WebSocketState.CloseReceived)
                    {
                        await websocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "", CancellationToken.None);
                        await Task.Delay(2000);
                    }
                }
                finally
                {
                    MicroServiceControllerBase.RequestingObject.Value = null;
                }
            }
        }

        async void keepAlive(WebSocket webSocket)
        {
            if (PingMethod == null)
            {
                var type = webSocket.GetType();


                //ValueTask valueTask = SendFrameAsync(MessageOpcode.Pong, endOfMessage: true, disableCompression: true, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

                PingMethod = type.GetMethod("SendFrameAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (PingMethod == null)
                {
                    _logger?.LogError("WebSocket没有SendFrameAsync成员");
                    return;
                }


                var pObjs = new object[5];

                var parameters = PingMethod.GetParameters();
                if (parameters.Length != pObjs.Length)
                {
                    _logger?.LogError($"SendFrameAsync参数不是{pObjs.Length}个");
                    return;
                }

                var opcodes = Enum.GetValues(parameters[0].ParameterType);
                for (int i = 0; i < opcodes.Length; i++)
                {
                    if (opcodes.GetValue(i).ToString() == "Ping")
                    {
                        pObjs[0] = opcodes.GetValue(i);
                        break;
                    }
                }
                pObjs[1] = true;
                pObjs[2] = true;
                pObjs[3] = ReadOnlyMemory<byte>.Empty;
                pObjs[4] = CancellationToken.None;
                PingMethodParameters = pObjs;
            }
            try
            {

                while (webSocket.State == WebSocketState.Open && PingMethod != null && PingMethodParameters != null)
                {
                    await Task.Delay(5000);
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await (dynamic)PingMethod.Invoke(webSocket, PingMethodParameters);
                    }

                }
            }
            catch
            {

            }
        }

        public static async Task<string> ReadHeaders(string preRequestString, NetClient client, IDictionary<string, string> headers)
        {
            List<byte> lineBuffer = new List<byte>(1024);
            string line = null;
            string requestPathLine = null;
            byte[] bData = new byte[1];
            int read;
            while (true)
            {
               read = await client.InnerStream.ReadAsync(bData , 0 , 1);
                if (read <= 0)
                    throw new SocketException();

                if (bData[0] == 10)
                {
                    line = Encoding.UTF8.GetString(lineBuffer.ToArray());
                    lineBuffer.Clear();
                    if (requestPathLine == null)
                        requestPathLine = preRequestString + line;

                    if (line == "")
                    {
                        break;
                    }
                    else if (line.Contains(":"))
                    {
                        var arr = line.Split(':');
                        if (arr.Length >= 2)
                        {
                            var key = arr[0].Trim();
                            var value = arr[1].Trim();
                            if (headers.ContainsKey(key) == false)
                            {
                                headers[key] = value;
                            }
                        }
                    }
                }
                else if (bData[0] != 13)
                {
                    lineBuffer.Add(bData[0]);
                }
            }
            return requestPathLine;
        }
    }
}
