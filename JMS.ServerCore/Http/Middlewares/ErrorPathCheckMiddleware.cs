﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace JMS.ServerCore.Http.Middlewares
{
    public class ErrorPathCheckMiddleware : IHttpMiddleware
    {
        public Task<bool> Handle(NetClient netClient, string httpMethod, string requestPath,IDictionary<string,string> headers)
        {
            if (requestPath.Contains("../"))
            {
                netClient.KeepAlive = false;
                netClient.OutputHttpNotFund();
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }
}
