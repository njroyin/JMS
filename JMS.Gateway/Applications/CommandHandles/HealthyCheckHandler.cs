﻿using JMS.Dtos;
using JMS.Domains;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace JMS.Applications.CommandHandles
{
    class HealthyCheckHandler : ICommandHandler
    {
        IServiceProvider _serviceProvider;
        public CommandType MatchCommandType => CommandType.HealthyCheck;
        Gateway _gateway;
        public HealthyCheckHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _gateway = _serviceProvider.GetService<Gateway>();
        }
        public void Handle(NetClient netclient, GatewayCommand cmd)
        {
            netclient.ReadTimeout = 30000;
            while (!_gateway.Disposed)
            {
                netclient.WriteServiceData(new InvokeResult { 
                    Success = true
                });
                netclient.ReadServiceObject<GatewayCommand>();                
            }
        }
    }
}
