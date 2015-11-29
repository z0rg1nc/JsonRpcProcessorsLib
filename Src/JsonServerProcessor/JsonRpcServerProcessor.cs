using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.MiscUtils;
using BtmI2p.Newtonsoft.Json;
using BtmI2p.Newtonsoft.Json.Linq;
using NLog;
using ObjectStateLib;

namespace BtmI2p.JsonRpcHelpers.Server
{
    [AttributeUsage(AttributeTargets.Method)]
    public class NeedUserConnectionContextAttribute : Attribute
    {
        public NeedUserConnectionContextAttribute(Type contextType)
        {
            ContextType = contextType;
        }

        public Type ContextType;
    }
    public static class ServerConnectionContext<TContext> where TContext : class
    {
        private static TContext _connectionContext = default(TContext);
        private static readonly SemaphoreSlim Lock = new SemaphoreSlim(1); //different for different TContext
        public static TContext GetContext()
        {
            try
            {
                return _connectionContext;
            }
            finally
            {
                _connectionContext = default(TContext);
                Lock.Release();
            }
        }

        private static Logger _logger = LogManager.GetCurrentClassLogger();
        public static async Task SetContext(TContext nextContext)
        {
            await Lock.WaitAsync().ConfigureAwait(false);
            _connectionContext = nextContext;
        }
    }
    public enum EProcessRequestErrs
    {
        ParseRequestStringError,
        ParseRequestJObjectError,
        ParseJsonRpcError,
        MethodNameNotFound, //id
        ParseParamEnumerableError, //id
        ParseParamItemError, //id
        NotEnoughParameters, //id
        InvokeError, //id
        RethrowableException, //id
        ConvertToReturnTypeError, //id,
        ParseParamItemNullError, //id
		JsonRpcException, //id
    }

    public class PrepareRequestData
    {
        public Func<object, object[], object> InvokeDelegate;
        public object[] MethodParamsArray;
        public string RequestId;
        //public  ReturnVoid;
        public Type ReturnType;
        public Type ResultType; //string if Task or Void, T1 if Task<T1>,T1
        public string CallMethodName;
        public bool NeedSetUserConnectionContext;
        public Type UserConnectionContextType;
    }
    [JsonObject(MemberSerialization.OptIn)]
    public class JsonRpcError
    {
        [JsonProperty("code")]
        public int ErrorCode { get; set; }
        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public static class JsonServerProcessorHelper
    {
        public static JObject GetResultJObject(
            string requestId, 
            object result, 
            Type resultType
        )
        {
            var resultObj = new JObject();
            resultObj["jsonrpc"] = "2.0";
            resultObj["id"] = requestId;
            try
            {
                resultObj["result"] = JToken.Parse(
                    result.WriteObjectToJson(resultType)
                );
            }
            catch (Exception exc)
            {
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.ConvertToReturnTypeError,
                    tag: requestId,
                    innerException: exc
                );
            }
            return resultObj;
        }
        public static JObject GetErrJson(EnumException<EProcessRequestErrs> errExc)
        {
            if (errExc == null)
                throw new ArgumentNullException("errExc");
            if (errExc.Tag == null)
            {
                errExc.Tag = 1;
            }
            var errJson = new JObject();
            errJson["jsonrpc"] = "2.0";
            errJson["id"] = null;
            var errDesc = new JsonRpcError();
            switch (errExc.ExceptionCode)
            {
                case EProcessRequestErrs.ParseRequestStringError:
                    errDesc.ErrorCode = -32701;
                    errDesc.Message = "Byte[] to string conversion error";
                    break;
                case EProcessRequestErrs.ParseRequestJObjectError:
                    errDesc.ErrorCode = -32700;
                    errDesc.Message = "Parse json object error";
                    break;
                case EProcessRequestErrs.ParseJsonRpcError:
                    errDesc.ErrorCode = -32600;
                    errDesc.Message = "Parse json-rpc request error";
                    break;
                case EProcessRequestErrs.MethodNameNotFound:
                    errDesc.ErrorCode = -32601;
                    errDesc.Message = "The method does not exist / is not available";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.ParseParamEnumerableError:
                    errDesc.ErrorCode = -32602;
                    errDesc.Message = "Parse params object error";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.ParseParamItemError:
                    errDesc.ErrorCode = -32602;
                    errDesc.Message = "Deserialize params item error";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.ParseParamItemNullError:
                    errDesc.ErrorCode = -32602;
                    errDesc.Message = "Deserialize params item error: null item";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.NotEnoughParameters:
                    errDesc.ErrorCode = -32602;
                    errDesc.Message = "Not enough parameters";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.InvokeError:
                    errDesc.ErrorCode = -32500;
                    errDesc.Message = "Method invoke error";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.ConvertToReturnTypeError:
                    errDesc.ErrorCode = -32603;
                    errDesc.Message = "Serialize method result error";
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
                case EProcessRequestErrs.RethrowableException:
                    var innerExc = (RpcRethrowableException)errExc.InnerException;
                    errDesc.ErrorCode = -32501;
                    errDesc.Message = JsonConvert.SerializeObject(innerExc.ErrorData);
                    errJson["id"] = JToken.FromObject(errExc.Tag);
                    break;
				case EProcessRequestErrs.JsonRpcException:
		            var innerJsonExc = (JsonRpcException) errExc.InnerException;
		            errDesc.ErrorCode = innerJsonExc.JsonErrorCode;
		            errDesc.Message = innerJsonExc.JsonErrorMessage;
					errJson["id"] = JToken.FromObject(errExc.Tag);
					break;
                default:
                    throw new Exception("Unknown exception code");
            }
            errJson["error"] = JToken.Parse(errDesc.WriteObjectToJson());
            return errJson;
        }
    }

    /// <summary>
    /// JsonRpc 2.0
    /// One instance, multithread calls without sync
    /// Request Id = int
    /// Error codes http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
    /// </summary>
    /// <typeparam name="TImpl"></typeparam>
    /// <typeparam name="TContext"></typeparam>
    public class JsonRpcServerProcessor<TImpl,TContext> where TContext : class
    {
        private readonly TImpl _impl;

        public class JsonMethodInfo
        {
            public int ParamCount = 0;
            public readonly List<Type> ParamTypes = new List<Type>();
            public readonly List<object> DefaultParams = new List<object>();
            public readonly List<bool> HasDefaultParams = new List<bool>();
            public readonly List<string> ParamNames = new List<string>();
            public Func<object, object[], object> InvokeDelegate = null;
            //public bool ReturnVoid = false;
            public Type ReturnType;
            public bool NeedUserConnectionContext = false;
            public MethodInfo RealImplMethodInfo;
            public MethodInfo InterfaceMethodInfo;
        }

        public JsonMethodInfo GetMethodInfo(string methodName)
        {
            return _methodInfos[methodName];
        }

        private readonly Dictionary<string, JsonMethodInfo> _methodInfos
            = new Dictionary<string, JsonMethodInfo>();
        
        public JsonRpcServerProcessor(TImpl serverImpl)
        {
            _impl = serverImpl;
            var serverImplTypeMethods = JsonRpcClientProcessor.GetPublicMethods(typeof(TImpl));
            var realType = serverImpl.GetType();
            foreach (MethodInfo methodInfo in serverImplTypeMethods)
            {
                var realMethodInfo = realType.GetMethod(
                    methodInfo.Name,
                    methodInfo.GetParameters().Select(x => x.ParameterType).ToArray()
                );
                if (realMethodInfo == null)
                    throw new Exception("Real service doesn't contain appropriate method");
                bool methodNeedsConnectionContext = Attribute.IsDefined(
                    realMethodInfo,
                    typeof(NeedUserConnectionContextAttribute)
                );
                if (methodNeedsConnectionContext)
                {
                    var contextAttribute = 
                        realMethodInfo.GetCustomAttribute<NeedUserConnectionContextAttribute>();
                    if(contextAttribute.ContextType != typeof(TContext))
                        throw new Exception("Wrong context type in NeedContext attribute");
                }
                var newMethodInfo = new JsonMethodInfo()
                {
                    InvokeDelegate = methodInfo.Invoke,
                    ReturnType = methodInfo.ReturnType,
                    NeedUserConnectionContext = methodNeedsConnectionContext,
                    RealImplMethodInfo = realMethodInfo,
                    InterfaceMethodInfo = methodInfo
                };
                foreach (ParameterInfo paramInfo in methodInfo.GetParameters())
                {
                    newMethodInfo.ParamCount++;
                    newMethodInfo.ParamTypes.Add(paramInfo.ParameterType);
                    if (paramInfo.HasDefaultValue)
                    {
                        newMethodInfo.DefaultParams.Add(paramInfo.DefaultValue);
                        newMethodInfo.HasDefaultParams.Add(true);
                    }
                    else
                    {
                        newMethodInfo.DefaultParams.Add(null);
                        newMethodInfo.HasDefaultParams.Add(false);
                    }
                    newMethodInfo.ParamNames.Add(paramInfo.Name);
                }

                _methodInfos.Add(methodInfo.Name, newMethodInfo);
            }
        }

        /* EnumException<EProcessRequestErrs> errExc
         * EProcessRequestErrs.InvokeVoidSuccessful with void return
         */
        public async Task<JObject> ProcessRequest(
            JObject data,
            TContext context = null
        ) 
        {
            if (data == null)
                throw new ArgumentNullException("data");
            try
            {
                var preparedRequestData = PrepareRequest(data);
                var requestId = preparedRequestData.RequestId;
                var result = await InvokeImplMethod(preparedRequestData, context).ConfigureAwait(false);
                return JsonServerProcessorHelper.GetResultJObject(
                    requestId,
                    result,
                    preparedRequestData.ResultType
                );
            }
            catch (EnumException<EProcessRequestErrs> errExc)
            {
                return JsonServerProcessorHelper.GetErrJson(errExc);
            }
        }
        

        public PrepareRequestData PrepareRequest(
            JObject requestJsonObject
        )
        {
            if(requestJsonObject == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => requestJsonObject));
            string requestId = null;
            JToken paramsToken = null;
            string methodName = null;
            foreach (KeyValuePair<string, JToken> pair in requestJsonObject)
            {
                if (pair.Key == "jsonrpc")
                {
                    if (pair.Value.ToString() != "2.0")
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                }
                else if (pair.Key == "id")
                {
                    if (requestId != null)
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                    try
                    {
                        requestId = pair.Value.ToString();
                    }
                    catch (Exception)
                    {
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                    }
                }
                else if (pair.Key == "params")
                {
                    if (paramsToken != null)
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                    paramsToken = pair.Value;
                }
                else if (pair.Key == "method")
                {
                    if (methodName != null)
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                    methodName = pair.Value.ToObject<string>();
                }
                else
                {
                    throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseJsonRpcError
                        );
                }
            }
            if (
                string.IsNullOrWhiteSpace(methodName)
            )
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.ParseJsonRpcError
                );
            string callMethodNaem = methodName;
            if (!_methodInfos.ContainsKey(methodName))
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.MethodNameNotFound,
                    tag: requestId
                );
            var callMethodInfo = _methodInfos[methodName];
            if (
                paramsToken == null &&
                callMethodInfo.ParamCount > 0 &&
                callMethodInfo.HasDefaultParams.Any(x => !x)
            )
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.ParseJsonRpcError
                );
            if (requestId == null && callMethodInfo.ReturnType != typeof(void))
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.ParseJsonRpcError
                );
            var methodParams = new List<object>(callMethodInfo.DefaultParams);
            var methodParamsSet = new List<bool>(callMethodInfo.HasDefaultParams);
            if (paramsToken != null)
            {
                if (paramsToken.Type == JTokenType.Object)
                {
                    var paramChildrens = paramsToken.Children().ToList();
                    if (paramChildrens.Count > callMethodInfo.ParamCount)
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseParamEnumerableError,
                            tag: requestId
                        );
                    foreach (var token in paramChildrens)
                    {
                        int i = callMethodInfo.ParamNames.IndexOf(((JProperty)token).Name);
                        if (i == -1)
                            throw new EnumException<EProcessRequestErrs>(
                                EProcessRequestErrs.ParseParamItemError,
                                tag: requestId
                            );
                        try
                        {
                            methodParams[i] = token.ToObject(callMethodInfo.ParamTypes[i]);
                            if (
                                !callMethodInfo.ParamTypes[i].IsValueType 
                                && methodParams[i] == null
                            )
                            {
                                throw new EnumException<EProcessRequestErrs>(
                                    EProcessRequestErrs.ParseParamItemNullError,
                                    tag: requestId
                                );
                            }
                            methodParamsSet[i] = true;
                        }
                        catch
                        {
                            throw new EnumException<EProcessRequestErrs>(
                                EProcessRequestErrs.ParseParamItemError,
                                tag: requestId
                            );
                        }
                    }
                }
                else if (paramsToken.Type == JTokenType.Array)
                {
                    try
                    {
                        var paramsArray = paramsToken.Children().ToList();
                        if (paramsArray.Count > callMethodInfo.ParamCount)
                            throw new EnumException<EProcessRequestErrs>(
                                EProcessRequestErrs.ParseParamEnumerableError,
                                tag: requestId
                                );
                        for (int i = 0; i < paramsArray.Count; i++)
                        {
                            try
                            {
                                methodParams[i] = paramsArray[i].ToObject(
                                    callMethodInfo.ParamTypes[i]
                                );
                                if (
                                    !callMethodInfo.ParamTypes[i].IsValueType
                                    && methodParams[i] == null
                                )
                                {
                                    throw new EnumException<EProcessRequestErrs>(
                                        EProcessRequestErrs.ParseParamItemNullError,
                                        tag: requestId
                                    );
                                }
                                methodParamsSet[i] = true;
                            }
                            catch
                            {
                                throw new EnumException<EProcessRequestErrs>(
                                    EProcessRequestErrs.ParseParamItemError,
                                    tag: requestId
                                );
                            }
                        }
                    }
                    catch (EnumException<EProcessRequestErrs>)
                    {
                        throw;
                    }
                    catch
                    {
                        throw new EnumException<EProcessRequestErrs>(
                            EProcessRequestErrs.ParseParamEnumerableError,
                            tag: requestId
                        );
                    }
                }
            }
            else
            {
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.ParseJsonRpcError
                );
            }
            if (methodParamsSet.Any(x => x == false))
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.NotEnoughParameters,
                    tag: requestId
                );
            Type resultType;
            if (callMethodInfo.ReturnType == typeof (void))
                resultType = typeof (string);
            else if (callMethodInfo.ReturnType == typeof(Task))
                resultType = typeof(string);
            else if (
                callMethodInfo.ReturnType.IsGenericType &&
                callMethodInfo.ReturnType.GetGenericTypeDefinition() == typeof (Task<>)
            )
                resultType = callMethodInfo.ReturnType.GetGenericArguments()[0];
            else
                resultType = callMethodInfo.ReturnType;
            return new PrepareRequestData()
            {
                InvokeDelegate = callMethodInfo.InvokeDelegate,
                MethodParamsArray = methodParams.ToArray(),
                RequestId = requestId,
                ReturnType = callMethodInfo.ReturnType,
                ResultType = resultType,
                CallMethodName = callMethodNaem,
                NeedSetUserConnectionContext = callMethodInfo.NeedUserConnectionContext
            };
        }

        private readonly static Logger _logger = LogManager.GetCurrentClassLogger();
        public async Task<object> InvokeImplMethod(
            PrepareRequestData preparedRequestData,
            TContext context = null,
            CancellationToken token = default(CancellationToken)
        )
        {
            var curMethodName = nameof(InvokeImplMethod);
            var invokeDelegate = preparedRequestData.InvokeDelegate;
            var methodParams = preparedRequestData.MethodParamsArray;
            try
            {
                if (preparedRequestData.NeedSetUserConnectionContext && context != null)
                    await ServerConnectionContext<TContext>.SetContext(context).ConfigureAwait(false);
                if (preparedRequestData.ReturnType == typeof (Task))
                {
                    var result = invokeDelegate(
                        _impl,
                        methodParams
                        );
                    await ((Task) result).ThrowIfCancelled(token).ConfigureAwait(false);
                    return "null";
                }
                if (
                    preparedRequestData.ReturnType.IsGenericType &&
                    preparedRequestData.ReturnType.GetGenericTypeDefinition() == typeof (Task<>)
                    )
                {
                    var result = invokeDelegate(
                        _impl,
                        methodParams
                        );
                    var resultType = preparedRequestData.ReturnType.GetGenericArguments()[0];
                    var getTaskResultMthd = JsonRpcClientProcessor.GetGenericResTaskToObjTaskMethodInfo(
                        resultType
                        );
                    return await ((Task<object>) getTaskResultMthd.Invoke(this, new[] {result})).ThrowIfCancelled(token).ConfigureAwait(false);
                }
                if (preparedRequestData.ReturnType == typeof (void))
                {
                    await Task.Factory.StartNew(
                        () => invokeDelegate(
                            _impl,
                            methodParams
                            ),
                        TaskCreationOptions.LongRunning
                        ).ThrowIfCancelled(token).ConfigureAwait(false);
                    return "null";
                }
                return await Task.Factory.StartNew(
                    () => invokeDelegate(
                        _impl,
                        methodParams
                        ),
                    TaskCreationOptions.LongRunning
                    ).ThrowIfCancelled(token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Error(
                    "{0} timeout exception",
                    curMethodName
                );
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.RethrowableException,
                    "",
                    preparedRequestData.RequestId,
                    RpcRethrowableException.Create(
                        EJsonRpcClientProcessorServiceErrCodes.RetryLaterRpcExcCode
                    )
                );
            }
            catch (WrongDisposableObjectStateException)
            {
                _logger.Trace(
                    "{0} wrong disposable object state exception",
                    curMethodName
                );
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.RethrowableException,
                    "",
                    preparedRequestData.RequestId,
                    RpcRethrowableException.Create(
                        EJsonRpcClientProcessorServiceErrCodes.RetryLaterRpcExcCode
                        )
                    );
            }
            catch (OperationCanceledException)
            {
                _logger.Trace(
                    "{0} operation cancelled exception",
                    curMethodName
                );
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.RethrowableException,
                    "",
                    preparedRequestData.RequestId,
                    RpcRethrowableException.Create(
                        EJsonRpcClientProcessorServiceErrCodes.RetryLaterRpcExcCode
                        )
                    );
            }
            catch (RpcRethrowableException innerExc)
            {
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.RethrowableException,
                    "",
                    preparedRequestData.RequestId,
                    innerExc
                    );
            }
            catch (JsonRpcException jsonRpcException)
            {
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.JsonRpcException,
                    "",
                    preparedRequestData.RequestId,
                    jsonRpcException
                    );
            }
            catch (TargetInvocationException invocationException)
            {
                /*_logger.Trace(
                    "TargetInvocationException invocationException: '{0}'",
                    invocationException.ToString()
                );*/
                if (
                    invocationException.InnerException is WrongDisposableObjectStateException
                    || invocationException.InnerException is OperationCanceledException
                    || invocationException.InnerException is TimeoutException
                )
                {
                    _logger.Trace(
                        "{0} operation cancelled|timeout|wrong disposable object state exception",
                        curMethodName
                    );
                    throw new EnumException<EProcessRequestErrs>(
                        EProcessRequestErrs.RethrowableException,
                        "",
                        preparedRequestData.RequestId,
                        RpcRethrowableException.Create(
                            EJsonRpcClientProcessorServiceErrCodes.RetryLaterRpcExcCode
                            )
                        );
                }
                /**/
                (
                    invocationException.InnerException
                        as RpcRethrowableException
                    ).With(
                        innerExc =>
                        {
                            if (innerExc != null)
                                throw new EnumException<EProcessRequestErrs>(
                                    EProcessRequestErrs.RethrowableException,
                                    "",
                                    preparedRequestData.RequestId,
                                    innerExc
                                    );
                        }
                    );
                /**/
                (
                    invocationException.InnerException
                        as JsonRpcException
                    ).With(
                        innerJsonExc =>
                        {
                            throw new EnumException<EProcessRequestErrs>(
                                EProcessRequestErrs.JsonRpcException,
                                "",
                                preparedRequestData.RequestId,
                                innerJsonExc
                                );
                        }
                    );
                _logger.Error(
                    "{0} unexpected error: '{1}'",
                    curMethodName,
                    invocationException.ToString()
                );
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.InvokeError,
                    innerException: invocationException,
                    tag: preparedRequestData.RequestId
                );
            }
            catch (Exception exc)
            {
                _logger.Error(
                    "{0} unexpected error: '{1}'",
                    curMethodName,
                    exc.ToString()
                );
                throw new EnumException<EProcessRequestErrs>(
                    EProcessRequestErrs.InvokeError,
                    innerException: exc,
                    tag: preparedRequestData.RequestId
                    );
            }
        }
    }
}
