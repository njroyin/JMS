﻿using JMS.Applications.HttpMiddlewares;
using JMS.Applications;
using JMS.Common;
using JMS.Infrastructures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using JMS.ServerCore.Http;
using JMS.ServerCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using JMS.Applications.HttpMiddlewares;
using Microsoft.Extensions.Logging;
using JMS.ApiDocument;
using JMS.Cluster;
using JMS.Authentication;

namespace JMS
{
    public class GatewayBuilder
    {
        private readonly string[] _args;

        public JmsServiceCollection Services { get;}
        public IConfiguration Configuration { get; private set; }
        private GatewayBuilder(string[] args)
        {
            Services = new JmsServiceCollection();
            _args = args;
        }

        public Gateway Build()
        {
            CommandArgParser cmdArg = new CommandArgParser(_args);

            var appSettingPath = cmdArg.TryGetValue<string>("-s");

            if (appSettingPath == null)
                appSettingPath = "appsettings.json";

            var builder = new ConfigurationBuilder();
            if (appSettingPath == "share")
            {
                appSettingPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                appSettingPath = Path.Combine(appSettingPath, "jms.gateway");
                if (Directory.Exists(appSettingPath) == false)
                {
                    Directory.CreateDirectory(appSettingPath);
                }
                appSettingPath = Path.Combine(appSettingPath, "appsettings.json");
                if (File.Exists(appSettingPath) == false)
                {
                    File.Copy("./appsettings.json", appSettingPath);
                }
            }

            builder.AddJsonFile(appSettingPath, optional: true, reloadOnChange: true);
            this.Configuration = builder.Build();

            var port = this.Configuration.GetValue<int>("Port");
            port = cmdArg.TryGetValue<int>("-p", port);

            DefaultGatewayEnvironment gatewayEnvironment = new DefaultGatewayEnvironment(appSettingPath, port);
            Services.AddSingleton<IGatewayEnvironment>(gatewayEnvironment);
            Services.AddSingleton<IConfiguration>(this.Configuration);


            var sharefolder = this.Configuration.GetValue<string>("ShareFolder");
            if (!System.IO.Directory.Exists(sharefolder))
            {
                System.IO.Directory.CreateDirectory(sharefolder);
            }

            var datafolder = this.Configuration.GetValue<string>("DataFolder");
            if (!System.IO.Directory.Exists(datafolder))
            {
                System.IO.Directory.CreateDirectory(datafolder);
            }


            Services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(this.Configuration.GetSection("Logging"));
                loggingBuilder.AddConsole(); // 将日志输出到控制台
            });

            Services.AddSingleton<ClusterGatewayConnector>();
            Services.AddSingleton<TransactionStatusManager>();
            Services.AddSingleton<IRequestReception, RequestReception>();
            Services.AddSingleton<IRegisterServiceManager, RegisterServiceManager>();
            Services.AddSingleton<ICommandHandlerRoute, CommandHandlerRoute>();
            Services.AddSingleton<IRemoteClientManager, DefaultRemoteClientManager>();
            Services.AddSingleton<Gateway>();
            Services.AddSingleton<LockKeyManager>();
            Services.AddSingleton<IAuthentication, DefaultAuthentication>();
            Services.AddTransient<IMicroServiceReception, MicroServiceReception>();
            Services.AddSingleton<FileChangeWatcher>();
            Services.AddTransient<ListenFileChangeReception>();
            Services.AddSingleton<ClientCheckFactory>();
            Services.AddSingleton<ErrorUserMarker>();
            Services.AddSingleton<IDocumentButtonProvider, DocumentButtonProvider>();

            //添加所有handler
            var interfaceType = typeof(ICommandHandler);
            var handleTypes = typeof(CommandHandlerRoute).Assembly.DefinedTypes.Where(m => m.ImplementedInterfaces.Contains(interfaceType)).Select(m => ServiceDescriptor.Singleton(interfaceType, m));
            Services.TryAddEnumerable(handleTypes);

            Services.UseHttp()
                .AddHttpMiddleware<WebSocketMiddleware>()
                .AddHttpMiddleware<FunctionRequestMiddleware>()
                .AddHttpMiddleware<JmsDocMiddleware>()
                .AddHttpMiddleware<ProxyMiddleware>();

            var assembly = Assembly.Load(this.Configuration.GetValue<string>("ServiceProviderAllocator:Assembly"));
            var serviceProviderAllocatorType = assembly.GetType(this.Configuration.GetValue<string>("ServiceProviderAllocator:FullName"));

            Services.AddSingleton(typeof(IServiceProviderAllocator), serviceProviderAllocatorType);

            //SSL
            var certPath = this.Configuration.GetValue<string>("SSL:Cert");
            if (!string.IsNullOrEmpty(certPath))
            {
                var serverCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, this.Configuration.GetValue<string>("SSL:Password"));
                var acceptCertHash = this.Configuration.GetSection("SSL:AcceptCertHash").Get<string[]>();
                Services.AddSingleton<ISslConfiguration>(new DefaultSslConfiguration(serverCert, acceptCertHash));
            }
            else
            {
                Services.AddSingleton<ISslConfiguration>(new DefaultSslConfiguration(null, null));
            }

            var serviceProvider = Services.BuildServiceProvider();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            serviceProvider.GetService<LockKeyManager>();
            serviceProvider.GetService<FileChangeWatcher>();
            serviceProvider.GetService<TransactionStatusManager>();
            serviceProvider.GetService<ICommandHandlerRoute>().Init();

            serviceProvider.GetService<IHttpMiddlewareManager>().PrepareMiddlewares(serviceProvider);

            var gateway = serviceProvider.GetService<Gateway>();

          

            gateway.ServiceProvider = serviceProvider;
            return gateway;
        }
        public static GatewayBuilder Create(string[] args)
        {
            GatewayBuilder builder = new GatewayBuilder(args);

            return builder;
        }
    }
}
