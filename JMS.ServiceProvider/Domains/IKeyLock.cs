﻿using System;
using System.Collections.Generic;
using System.Text;

namespace JMS
{
    public interface IKeyLocker
    {
        /// <summary>
        /// 申请锁住指定的key，使用完毕务必调用UnLock释放
        /// </summary>
        /// <param name="transactionId">在controller中，事务id属于纯数字类型，如果不是在controller中调用lock，事务id可以用字母+线程ID组合，避免和其他请求重复</param>
        /// <param name="key"></param>
        /// <returns>是否成功</returns>
        bool TryLock(string transactionId, string key);
        bool TryUnLock(string transactionId, string key);
        /// <summary>
        /// 强制释放锁定的key（慎用）
        /// </summary>
        /// <param name="key"></param>
        void UnLockAnyway(string key);
        string[] GetLockedKeys();
        void RemoveKeyFromLocal(string key);
        
    }
}
