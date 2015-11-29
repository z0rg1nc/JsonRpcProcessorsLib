using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using LinFu.DynamicProxy;

namespace BtmI2p.JsonRpcHelpers.Client
{
    public class WrapperErrorHandlingSettings : ICheckable
    {
        public Action<InvocationInfo, Exception> AdvancedErrorHandling;
        public void CheckMe()
        {
            if(AdvancedErrorHandling == null)
                throw new ArgumentNullException(
                    this.MyNameOfProperty(e => e.AdvancedErrorHandling));
        }
    }

    public class WrapperErrorHandling<T1> : IInvokeWrapper
        where T1 : class
    {
        private readonly WrapperErrorHandlingSettings _settings;
        private readonly T1 _impl;
        public WrapperErrorHandling(
            WrapperErrorHandlingSettings settings,
            T1 impl
        )
        {
            if(impl == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => impl));
            if(settings == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => settings));
            try
            {
                settings.CheckMe();
            }
            catch (Exception exc)
            {
                throw new ArgumentException(
                    MyNameof.GetLocalVarName(() => settings),
                    exc
                );
            }
            _settings = settings;
            _impl = impl;
        }

        public T1 GetProxy()
        {
            var pF = new ProxyFactory();
            return pF.CreateProxy<T1>(this);
        }

        public void BeforeInvoke(InvocationInfo info)
        {
        }

        public object DoInvoke(InvocationInfo info)
        {
            return JsonRpcClientProcessor.DoInvokeHelper(
                info,
                DoInvokeImpl
            );
        }
        private readonly ConcurrentDictionary<Type,MethodInfo> _getProperObjTaskCache
            = new ConcurrentDictionary<Type, MethodInfo>();
        private async Task<object> DoInvokeImpl(InvocationInfo info)
        {
            try
            {
                return await JsonRpcClientProcessor.GetInvokeResultFromImpl(
                    _impl,
                    info
                ).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                _settings.AdvancedErrorHandling(info, exc);
                throw;
            }
        }

        public void AfterInvoke(InvocationInfo info, object returnValue)
        {
        }
    }
}
