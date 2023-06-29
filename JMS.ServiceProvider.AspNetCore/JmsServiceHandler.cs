﻿using JMS.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Way.Lib;

namespace JMS.ServiceProvider.AspNetCore
{
    internal class JmsServiceHandler
    {
        public static async Task<bool> Handle(IApplicationBuilder app, HttpContext context)
        {
            using (var netClient = new NetClient(new ConnectionStream(context)))
            {
                try
                {
                    context.Request.Headers.TryGetValue("TranId", out StringValues tranIds);
                    context.Request.Headers.TryGetValue("Tran", out StringValues supportTransactionStrs);
                    context.Request.Headers.TryGetValue("TranFlag", out StringValues tranFlag);

                    List<ApiTransactionDelegate> tranDelegateList = null;
                    var supportTran = !(supportTransactionStrs.Count > 0 && supportTransactionStrs[0] == "0");
                    ApiTransactionDelegate.CurrentTranId.Value = (tranIds[0], tranFlag.Count == 0 ? null : tranFlag[0]);
                    var controllerFactory = app.ApplicationServices.GetService<ControllerFactory>();

                    var parametersStrArr = (await netClient.ReadServiceDataAsync()).FromJson<string[]>();
                    var requestPath = context.Request.Path.Value;

                    while (true)
                    {
                        var controller = controllerFactory.Create(requestPath, context.RequestServices, out ControllerActionDescriptor desc);
                        if (desc == null)
                        {
                            netClient.WriteServiceData(new InvokeResult
                            {
                                Success = false,
                                Error = $"找不到{context.Request.Path.Value}对应的action",

                            });
                            releaseNetClient(netClient);
                            return true;
                        }
                        var author = desc.MethodInfo.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
                        if (author == null && desc.MethodInfo.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() == null)
                            author = desc.ControllerTypeInfo.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();

                        if (author != null)
                        {
                            try
                            {
                                var authRet = await context.AuthenticateAsync(author.AuthenticationSchemes);

                                if (authRet.Succeeded == false)
                                {
                                    netClient.WriteServiceData(new InvokeResult
                                    {
                                        Success = false,
                                        Error = "Authentication failed",

                                    });
                                    releaseNetClient(netClient);
                                    return true;
                                }
                                context.User = authRet.Principal;
                            }
                            catch (Exception ex)
                            {
                                netClient.WriteServiceData(new InvokeResult
                                {
                                    Success = false,
                                    Error = ex.Message,

                                });
                                releaseNetClient(netClient);
                                return true;
                            }
                        }


                        if (controller != null)
                        {
                            var parameters = new object[desc.Parameters.Count];
                            for (int i = 0; i < parameters.Length && i < parametersStrArr.Length; i++)
                            {
                                string pvalue = parametersStrArr[i];
                                if (pvalue == null)
                                    continue;

                                try
                                {
                                    parameters[i] = Newtonsoft.Json.JsonConvert.DeserializeObject(pvalue, desc.Parameters[i].ParameterType);
                                }
                                catch (Exception ex)
                                {
                                    var msg = $"转换参数出错，name:{desc.Parameters[i].Name} value:{pvalue} err:{ex.Message}";
                                    netClient.WriteServiceData(new InvokeResult
                                    {
                                        Success = false,
                                        Error = msg,

                                    });
                                    releaseNetClient(netClient);
                                    return true;
                                }
                            }

                            var actionFilterProcessor = new ActionFilterProcessor(context, controller, desc, parameters);
                            var tranDelegate = context.RequestServices.GetService<ApiTransactionDelegate>();
                            try
                            {

                                actionFilterProcessor.OnActionExecuting();
                                var result = desc.MethodInfo.Invoke(controller, parameters);
                                if (result is Task || result is ValueTask)
                                {
                                    if (desc.MethodInfo.ReturnType.IsGenericType)
                                    {
                                        result = await (dynamic)result;
                                    }
                                    else
                                    {
                                        await (dynamic)result;
                                        result = null;
                                    }
                                }

                                result = actionFilterProcessor.OnActionExecuted(result);
                                if (result != null)
                                {
                                    if (result is ObjectResult oret)
                                    {
                                        result = oret.Value;
                                    }
                                    else if (result is JsonResult jret)
                                    {
                                        result = jret.Value;
                                    }
                                    else if (result is StatusCodeResult statusCodeResult)
                                    {
                                        if (statusCodeResult.StatusCode < 200 || statusCodeResult.StatusCode >= 300)
                                        {
                                            if (tranDelegate.RollbackAction != null)
                                            {
                                                tranDelegate.RollbackAction();
                                                tranDelegate.RollbackAction = null;
                                                tranDelegate.CommitAction = null;
                                            }
                                            netClient.WriteServiceData(new InvokeResult
                                            {
                                                Success = false,
                                                Error = ((HttpStatusCode)statusCodeResult.StatusCode).ToString()

                                            });
                                            releaseNetClient(netClient);
                                            return true;
                                        }
                                        else
                                            result = statusCodeResult.StatusCode;
                                    }
                                    else if (result is OkObjectResult okobjret)
                                    {
                                        result = okobjret.Value;
                                    }
                                    else if (result is IActionResult)
                                        throw new Exception("不支持返回值类型" + result.GetType().FullName);
                                }
                                else
                                {
                                    result = null;
                                }

                                if (tranDelegate.CommitAction != null && !supportTran)
                                {
                                    tranDelegate.CommitAction();
                                    tranDelegate = null;
                                }
                                else if (tranDelegate.CommitAction == null)
                                {
                                    tranDelegate = null;
                                    supportTran = false;
                                }

                                var outputObj = new InvokeResult
                                {
                                    Success = true,
                                    SupportTransaction = supportTran,
                                    Data = result

                                };
                                netClient.WriteServiceData(outputObj);

                                if (!supportTran)
                                {
                                    releaseNetClient(netClient);
                                    return true;
                                }

                                tranDelegate.InvokeInfo = new InvokeInfo()
                                {
                                    ActionName = desc.ActionName,
                                    ControllerFullName = desc.ControllerTypeInfo.FullName,
                                    Parameters = parametersStrArr
                                };

                                tranDelegate.UserContent = context.User;

                                var failbuilder = app.ApplicationServices.GetService<ApiFaildCommitBuilder>();
                                var gatewayConnector = app.ApplicationServices.GetService<IGatewayConnector>();
                                var nextRequestPath = await tranDelegate.WaitForCommandAsync(null, gatewayConnector, failbuilder, netClient, app.ApplicationServices.GetService<ILogger>());

                                if (nextRequestPath != null)
                                {
                                    bool addToList = true;
                                    if (tranDelegateList == null)
                                    {
                                        tranDelegateList = new List<ApiTransactionDelegate>();
                                    }
                                    else
                                    {
                                        if (tranDelegate.StorageEngine != null)
                                        {
                                            foreach (var preDelegate in tranDelegateList)
                                            {
                                                if (preDelegate.StorageEngine == tranDelegate.StorageEngine)
                                                {
                                                    //同一个数据库对象，不用放入list
                                                    addToList = false;
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    if (addToList)
                                    {
                                        tranDelegateList.Add(tranDelegate);
                                    }
                                    //继续调用下一个方法
                                    requestPath = nextRequestPath;
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                while (ex.InnerException != null)
                                    ex = ex.InnerException;

                                actionFilterProcessor.Exception = ex;
                                actionFilterProcessor.OnActionExecuted(null);

                                if (tranDelegate != null)
                                {
                                    if (tranDelegate.StorageEngine == null || tranDelegateList == null || tranDelegateList.Any(x => x.StorageEngine == tranDelegate) == false)
                                    {
                                        tranDelegate.RollbackTransaction();
                                    }
                                    tranDelegate = null;
                                }
                                tranDelegateList.RollbackTransaction();

                                var outputObj = new InvokeResult
                                {
                                    Success = false,
                                    Error = ex.Message

                                };
                                netClient.WriteServiceData(outputObj);
                            }
                            releaseNetClient(netClient);
                        }
                    }
                }
                catch(Exception ex)
                {
                    var outputObj = new InvokeResult
                    {
                        Success = false,
                        Error = ex.Message

                    };
                    netClient.WriteServiceData(outputObj);
                    releaseNetClient(netClient);
                }
                return true;
            }
        }

        static void releaseNetClient(NetClient client)
        {
            client.Socket = null;
            client.InnerStream = null;
        }
    }
}
