﻿using JMS.Domains;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using Way.Lib;
using Microsoft.Extensions.DependencyInjection;
using JMS.Dtos;
using System.Linq;
using System.Reflection;
using JMS.RetryCommit;
using System.IO;
using System.Threading;

namespace JMS.Applications
{
    class InvokeRequestHandler : IRequestHandler
    {
        ControllerFactory _controllerFactory;
        IGatewayConnector _gatewayConnector;
        FaildCommitBuilder _faildCommitBuilder;
        MicroServiceHost _MicroServiceProvider;
        ILogger<InvokeRequestHandler> _logger;
        ILogger<TransactionDelegate> _loggerTran;
        public InvokeRequestHandler(ILogger<InvokeRequestHandler> logger,
             ILogger<TransactionDelegate> loggerTran,
            MicroServiceHost microServiceProvider,
            IGatewayConnector gatewayConnector,
            ControllerFactory controllerFactory,
            FaildCommitBuilder faildCommitBuilder)
        {
            this._controllerFactory = controllerFactory;
            this._gatewayConnector = gatewayConnector;
            this._faildCommitBuilder = faildCommitBuilder;
            _MicroServiceProvider = microServiceProvider;
            _logger = logger;
            _loggerTran = loggerTran;
        }

        public InvokeType MatchType => InvokeType.Invoke;
        static string LastInvokingMsgString;
        static DateTime LastInvokingMsgStringTime = DateTime.Now.AddDays(-1);
        public void Handle(NetClient netclient, InvokeCommand cmd)
        {
            var originalTimeout = netclient.ReadTimeout;
            TransactionDelegate transactionDelegate = null;
            MicroServiceControllerBase controller = null;
            object[] parameters = null;
            using (IServiceScope serviceScope = _MicroServiceProvider.ServiceProvider.CreateScope())
            {
                try
                {
                    MicroServiceControllerBase.RequestingObject.Value = new MicroServiceControllerBase.ThreadLocalObject(cmd,serviceScope.ServiceProvider);
                    var controllerTypeInfo = _controllerFactory.GetControllerType(cmd.Service);

                    object userContent = null;
                    if (controllerTypeInfo.NeedAuthorize)
                    {
                        var auth = _MicroServiceProvider.ServiceProvider.GetService<IAuthenticationHandler>();
                        if (auth != null)
                        {
                            userContent = auth.Authenticate(cmd.Header);
                        }
                    }

                    controller = _controllerFactory.CreateController(serviceScope, controllerTypeInfo);
                    controller.UserContent = userContent;
                    controller.NetClient = netclient;
                    controller._keyLocker = _MicroServiceProvider.ServiceProvider.GetService<IKeyLocker>();
                    if (_logger != null && _logger.IsEnabled(LogLevel.Trace))
                    {
                        var str = string.Format("invoke service:{0} method:{1} parameters:{2}", cmd.Service, cmd.Method, cmd.Parameters.ToJsonString());
                        if (str != LastInvokingMsgString || (DateTime.Now - LastInvokingMsgStringTime).TotalSeconds > 5)
                        {
                            LastInvokingMsgStringTime = DateTime.Now;
                            LastInvokingMsgString = str;
                            _logger?.LogTrace(str);
                        }
                    }

                    var methodInfo = controllerTypeInfo.Methods.FirstOrDefault(m => m.Method.Name == cmd.Method);
                    if (methodInfo == null)
                        throw new Exception($"{cmd.Service}没有提供{cmd.Method}方法");

                    if (methodInfo.NeedAuthorize && userContent == null)
                    {
                        var auth = _MicroServiceProvider.ServiceProvider.GetService<IAuthenticationHandler>();
                        if (auth != null)
                        {
                            userContent = auth.Authenticate(cmd.Header);
                        }
                    }

                    MicroServiceControllerBase.Current = controller;

                    var parameterInfos = methodInfo.Method.GetParameters();
                    object result = null;

                    int startPIndex = 0;
                    if (parameterInfos.Length > 0)
                    {
                        parameters = new object[parameterInfos.Length];
                        if (parameterInfos[0].ParameterType == typeof(TransactionDelegate))
                        {
                            startPIndex = 1;
                            parameters[0] = transactionDelegate = new TransactionDelegate(controller);
                            transactionDelegate.RequestCommand = cmd;
                        }
                    }

                    controller.OnBeforeAction(cmd.Method, parameters);
                    if (parameterInfos.Length > 0)
                    {
                        for (int i = startPIndex, index = 0; i < parameters.Length && index < cmd.Parameters.Length; i++, index++)
                        {
                            string pvalue = cmd.Parameters[index];
                            if (pvalue == null)
                                continue;

                            try
                            {
                                parameters[i] = Newtonsoft.Json.JsonConvert.DeserializeObject(pvalue, parameterInfos[i].ParameterType);
                            }
                            catch (Exception)
                            {
                                _logger?.LogError("转换参数出错，name:{0} cmd:{1}", parameterInfos[i].Name, cmd.ToJsonString());
                            }

                        }

                    }
                    result = methodInfo.Method.Invoke(controller, parameters);
                    controller.OnAfterAction(cmd.Method, parameters);

                    var supportTran = false;
                    if (transactionDelegate != null && (transactionDelegate.CommitAction != null || transactionDelegate.RollbackAction != null))
                    {
                        supportTran = true;
                    }
                    else if (controller.TransactionControl != null && (controller.TransactionControl.CommitAction != null || controller.TransactionControl.RollbackAction != null))
                    {
                        transactionDelegate = controller.TransactionControl;
                        transactionDelegate.RequestCommand = cmd;
                        supportTran = true;
                    }

                    if (supportTran && cmd.Header.ContainsKey("Tran") && cmd.Header["Tran"] == "0")
                    {
                        //调用端不需要事务支持
                        supportTran = false;
                    }

                    var resultdata = Encoding.UTF8.GetBytes(new InvokeResult
                    {
                        Success = true,
                        SupportTransaction = supportTran,
                        Data = result

                    }.ToJsonString());

                    if (!supportTran && transactionDelegate != null)
                    {
                        //不需要事务支持，提交现有事务
                        if (transactionDelegate.CommitAction != null)
                        {
                            transactionDelegate.CommitAction();
                        }
                        transactionDelegate = null;
                    }

                    netclient.WriteServiceData(resultdata);

                    if (!supportTran)
                    {
                        return;
                    }

                    if (transactionDelegate != null)
                    {
                        netclient.ReadTimeout = 0;
                        transactionDelegate.UserContent = controller.UserContent;
                        transactionDelegate.WaitForCommand(_gatewayConnector, _faildCommitBuilder, netclient, _loggerTran);
                        transactionDelegate = null;
                    }
                }
                catch (ResponseEndException)
                {
                }
                catch (Exception ex)
                {
                    if (ex is TargetInvocationException && ex.InnerException != null)
                        ex = ex.InnerException;

                    if (transactionDelegate != null)
                    {
                        try
                        {
                            if (transactionDelegate.RollbackAction != null)
                            {
                                transactionDelegate.RollbackAction();
                                _loggerTran?.LogInformation("事务{0}回滚完毕，请求数据:{1}", transactionDelegate.TransactionId, transactionDelegate.RequestCommand.ToJsonString());
                            }
                        }
                        catch (Exception rollex)
                        {
                            _logger?.LogError(rollex, rollex.Message);
                        }
                        transactionDelegate = null;
                    }

                    try
                    {
                        if (controller?.OnInvokeError(cmd.Method, parameters, ex) == false)
                        {
                            _logger?.LogError(ex, ex.Message);
                        }
                    }
                    catch (ResponseEndException)
                    {
                        return;
                    }

                    while (ex.InnerException != null)
                    {
                        ex = ex.InnerException;
                    }
                    netclient.WriteServiceData(new InvokeResult
                    {
                        Success = false,
                        Error = ex.Message
                    });
                    return;
                }
                finally
                {
                    netclient.ReadTimeout = originalTimeout;
                    MicroServiceControllerBase.Current = null;
                    controller?.OnUnLoad();
                    MicroServiceControllerBase.RequestingObject.Value = null;
                }
            }
        }
    }
}
