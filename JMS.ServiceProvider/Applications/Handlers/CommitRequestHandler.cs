﻿using JMS.Dtos;
using JMS.Domains;
using System;
using System.Collections.Generic;
using System.Text;
using Way.Lib;

namespace JMS.Applications
{
    class CommitRequestHandler : IRequestHandler
    {

        TransactionDelegateCenter _transactionDelegateCenter;
        public CommitRequestHandler(TransactionDelegateCenter transactionDelegateCenter)
        {
            _transactionDelegateCenter = transactionDelegateCenter;
        }

        public InvokeType MatchType => InvokeType.CommitTranaction;

        public void Handle(NetClient netclient, InvokeCommand cmd)
        {
            var ret = _transactionDelegateCenter.Commit(cmd.Header["TranId"]);
            netclient.WriteServiceData(new InvokeResult() { Success = ret });
        }
    }
}
