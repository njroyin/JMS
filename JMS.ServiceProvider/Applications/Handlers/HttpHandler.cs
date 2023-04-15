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
        IConnectionCounter _connectionCounter;
        public InvokeType MatchType => InvokeType.Http;

        static MethodInfo PingMethod;
        static object[] PingMethodParameters;
        public HttpHandler(ControllerFactory controllerFactory, MicroServiceHost microServiceProvider)
        {
            this._MicroServiceProvider = microServiceProvider;
            this._controllerFactory = controllerFactory;
            this._logger = microServiceProvider.ServiceProvider.GetService<ILogger<HttpHandler>>();
            _connectionCounter = microServiceProvider.ServiceProvider.GetService<IConnectionCounter>();
        }

       

        public async Task Handle(NetClient netclient, InvokeCommand cmd)
        {
            cmd.Header = new Dictionary<string, string>();
            var urlLine = await JMS.ServerCore.HttpHelper.ReadHeaders(cmd.Service, netclient.InnerStream, cmd.Header);



            string subProtocol = null;
            cmd.Header.TryGetValue("Sec-WebSocket-Protocol", out subProtocol);//[Connection, Upgrade] //Upgrade, websocket



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
                var responseText = JMS.ServerCore.HttpHelper.GetWebSocketResponse(cmd.Header , ref subProtocol);
                netclient.InnerStream.Write(Encoding.UTF8.GetBytes(responseText));
            }
            catch (Exception)
            {
                return;
            }

            WebSocket websocket = null;
            try
            {
                netclient.ReadTimeout = 0;
                websocket = WebSocket.CreateFromStream(netclient.InnerStream, true, subProtocol, Timeout.InfiniteTimeSpan);
                _connectionCounter.WebSockets.TryAdd(websocket, true);

                 using (IServiceScope serviceScope = _MicroServiceProvider.ServiceProvider.CreateScope())
                {
                    MicroServiceControllerBase.RequestingObject.Value =
                        new MicroServiceControllerBase.LocalObject(netclient.RemoteEndPoint, cmd, serviceScope.ServiceProvider, userContent, path);

                    var controller = (WebSocketController)_controllerFactory.CreateController(serviceScope, controllerTypeInfo);
                    controller.SubProtocol = subProtocol;

                    await controller.OnConnected(websocket);
                }

                if (websocket.State == WebSocketState.Open || websocket.State == WebSocketState.CloseReceived)
                {
                    await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch (Exception)
            {
                if (websocket != null)
                {
                    if (websocket.State == WebSocketState.Open || websocket.State == WebSocketState.CloseReceived)
                    {
                        await websocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "", CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (websocket != null)
                {
                    _connectionCounter.WebSockets.TryRemove(websocket, out bool o);
                    websocket.Dispose();
                }
                MicroServiceControllerBase.RequestingObject.Value = null;
            }

        }

    }
}
