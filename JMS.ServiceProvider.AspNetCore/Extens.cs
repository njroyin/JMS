﻿using JMS;
using JMS.ServiceProvider.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Ocsp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Way.Lib;
using static Microsoft.AspNetCore.Hosting.Internal.HostingApplication;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extens
    {
        static ConcurrentDictionary<IServiceCollection, MicroServiceHost> Hosts = new ConcurrentDictionary<IServiceCollection, MicroServiceHost>();
        static NetAddress[] Gateways;
        static IConnectionCounter ConnectionCounter;
        /// <summary>
        /// 把web server注册为JMS微服务
        /// </summary>
        /// <param name="services"></param>
        /// <param name="webServerUrl">web服务器的根访问路径，如 http://192.168.2.128:8080</param>
        /// <param name="serviceName">服务名称</param>
        /// <param name="gateways">网关地址</param>
        /// <param name="allowGatewayProxy">允许通过网关反向代理访问此服务</param>
        /// <param name="configOption">配置更多可选项</param>
        /// <param name="sslConfig">配置ssl证书</param>
        /// <returns></returns>
        public static IServiceCollection RegisterJmsService(this IServiceCollection services, string webServerUrl, string serviceName, NetAddress[] gateways,bool allowGatewayProxy = false, Action<IMicroServiceOption> configOption = null, Action<SSLConfiguration> sslConfig = null)
        {
           return RegisterJmsService(services,webServerUrl,serviceName,null,gateways ,allowGatewayProxy, configOption,sslConfig);
        }

        /// <summary>
        /// 把web server注册为JMS微服务
        /// </summary>
        /// <param name="services"></param>
        /// <param name="webServerUrl">web服务器的根访问路径，如 http://192.168.2.128:8080</param>
        /// <param name="serviceName">服务名称</param>
        /// <param name="description">服务描述</param>
        /// <param name="gateways">网关地址</param>
        /// <param name="allowGatewayProxy">允许通过网关反向代理访问此服务</param>
        /// <param name="configOption">配置更多可选项</param>
        /// <param name="sslConfig">配置ssl证书</param>
        /// <returns></returns>
        public static IServiceCollection RegisterJmsService(this IServiceCollection services, string webServerUrl, string serviceName,string description, NetAddress[] gateways, bool allowGatewayProxy = false, Action<IMicroServiceOption> configOption = null, Action<SSLConfiguration> sslConfig = null)
        {
            MicroServiceHost host = null;
            if (Hosts.ContainsKey(services) == false)
            {
                services.AddScoped<ApiTransactionDelegate>();
                services.AddSingleton<ApiRetryCommitMission>();
                services.AddSingleton<ApiFaildCommitBuilder>();
                services.AddSingleton<ControllerFactory>();
                services.AddScoped<IScopedKeyLocker, DefaultAspNetScopedKeyLocker>();

                 Gateways = gateways;
                host = new MicroServiceHost(services);
                services.AddSingleton<MicroServiceHost>(host);
               
                Hosts[services] = host;
            }
            else
            {
                host = Hosts[services];
            }

            if(sslConfig != null)
            {
                host.UseSSL(sslConfig);                
            }
            if (configOption != null)
            {
                configOption(host);
            }
            host.RegisterWebServer(webServerUrl, serviceName,description , allowGatewayProxy);
            return services;
        }

        /// <summary>
        /// 启动JMS微服务
        /// </summary>
        /// <param name="app"></param>
        /// <param name="onRegister">当微服务注册成功后的回调事件</param>
        /// <returns></returns>
        public static IApplicationBuilder UseJmsService(this IApplicationBuilder app,Action onRegister = null)
        {
           
            var host = app.ApplicationServices.GetService<MicroServiceHost>();
            if (host == null)
            {
                throw new Exception("请先调用services.RegisterJmsService() 注册服务");
            }


            host.ServiceProviderBuilded += (s, e) => {
                var retryEngine = app.ApplicationServices.GetService<ApiRetryCommitMission>();
                retryEngine.OnGatewayReady();
                onRegister?.Invoke();
            };

            host.Build(0, Gateways).Run(app.ApplicationServices);
            app.Use(async (context, next) =>
            {
                if (ConnectionCounter == null)
                {
                    ConnectionCounter = app.ApplicationServices.GetService<IConnectionCounter>();
                }

                ConnectionCounter.OnConnect();
                try
                {
                    if (await HttpHandler.Handle(app, context) == false)
                    {
                        await next();
                    }
                }
                catch (Exception)
                {

                    throw;
                }
                finally
                {
                    ConnectionCounter.OnDisconnect();
                }
            });



            return app;
        }

    }
}
