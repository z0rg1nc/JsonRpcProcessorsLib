using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using BtmI2p.Newtonsoft.Json;
using BtmI2p.Newtonsoft.Json.Linq;
using LinFu.DynamicProxy;
using NLog;

namespace BtmI2p.JsonRpcHelpers.Client
{
    public class RetryLaterCommandFromServerTimeoutException : TimeoutException
    {
    }

    public class RpcRethrowableExceptionData
    {
        public int ErrorCode = -1; //Unknown
        public string ErrorMessage = "";
        public byte[] AdditionalData = new byte[0];
    }

	public class JsonRpcException : Exception
	{
	    public int JsonErrorCode = -1;
		public string JsonErrorMessage = "";

		public override string Message => $"{JsonErrorCode}: {JsonErrorMessage}";

        public static JsonRpcException Create<T1>(EnumException<T1> originExc)
            where T1 : struct, IConvertible
        {
            if (!typeof(T1).IsEnum)
            {
                throw new ArgumentException("T1 isn't enum");
            }
            return new JsonRpcException()
            {
                JsonErrorCode = originExc.ExceptionCode.ToInt32(CultureInfo.InvariantCulture),
                JsonErrorMessage = originExc.InnerMessage
            };
        }
    }
	public class RpcRethrowableException : Exception
    {
        public RpcRethrowableException(
            RpcRethrowableExceptionData errorData
        ) : base(errorData.ErrorMessage)
        {
            ErrorData = errorData;
        }
        public RpcRethrowableException(
            int errorCode,
            string errorMessage = "",
            byte[] additionalData = null
        ) : base(errorMessage)
        {
            ErrorData = new RpcRethrowableExceptionData()
            {
                AdditionalData = additionalData,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
        }

		public static RpcRethrowableException Create<T1>(
			EnumException<T1> originExc
		)
			where T1 : struct, IConvertible
		{
			if (!typeof(T1).IsEnum)
			{
				throw new ArgumentException("T1 isn't enum");
			}
			return Create(
				originExc.ExceptionCode,
                originExc.InnerMessage
			);
		}

	    public static RpcRethrowableException Create(
	        int errorEnumCode
        )
	    {
	        return new RpcRethrowableException(errorEnumCode);
	    }

	    public static RpcRethrowableException Create<T1>(
            T1 errorEnumCode,
            string errorMessage = "",
            byte[] additionalData = null
        ) where T1 : struct, IConvertible
        {
            if (!typeof(T1).IsEnum)
            {
                throw new ArgumentException("T1 isn't enum and value type");
            }
            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = $"{typeof (T1).Name}.{errorEnumCode}";
            return new RpcRethrowableException(
                errorEnumCode.ToInt32(CultureInfo.InvariantCulture),
                errorMessage,
                additionalData
            );
        }

        public RpcRethrowableExceptionData ErrorData;
    }

    public class JsonRpcServerMethodParamInfo
    {
        public string ParamName;
        //public string ParamTypeFullName;
        public string ParamTypeName;
    }
    public class JsonRpcServerMethodInfo
    {
        public JsonRpcServerMethodInfo()
        {
        }

        public JsonRpcServerMethodInfo(MethodInfo info)
        {
            MethodName = info.Name;
            ReturnTypeName = info.ReturnType.Name;
            ParamsInfo = info
                .GetParameters()
                .Select(x => new JsonRpcServerMethodParamInfo()
                {
                    ParamName = x.Name,
                    ParamTypeName = x.ParameterType.Name
                })
                .ToList();
        }

        public static IEnumerable<JsonRpcServerMethodInfo> GetMethodInfos(Type t)
        {
            return JsonRpcClientProcessor.GetPublicMethods(t).Select(
                info => new JsonRpcServerMethodInfo(info)
            );
        }

        public string MethodName;
        //public string ReturnTypeFullName;
        public string ReturnTypeName;
        // (paramName, full name of paramType)
        public List<JsonRpcServerMethodParamInfo> ParamsInfo;
    }

    public enum EJsonRpcClientProcessorServiceErrCodes
    {
        UnknownErrorRpcExcCode = -1,
        CommunicationErrorRpcExcCode = -2,
        RetryLaterRpcExcCode = 1200000
    }

    public static class JsonRpcClientProcessorConstants
    {
        public static readonly TimeSpan RetryLaterTimeout 
            = TimeSpan.FromSeconds(5.0d);
    }
    public static class JsonRpcClientProcessor
    {
        public static bool CheckRpcServerMethodInfos(
            Type typeUnderTesting,
            List<JsonRpcServerMethodInfo> serverMethodInfos
            )
        {
            var serverMethodInfosDict = serverMethodInfos.ToDictionary(
                x => x.MethodName,
                 x => x
            );
            var underTestingTypeMethodInfos = GetPublicMethods(typeUnderTesting);
            foreach (MethodInfo info in underTestingTypeMethodInfos)
            {
                if(!serverMethodInfosDict.ContainsKey(info.Name))
                    return false;
                var serverMethodInfo = serverMethodInfosDict[info.Name];
                if (serverMethodInfo.ReturnTypeName != info.ReturnType.Name)
                    return false;
                var testingParamsInfo = info.GetParameters();
                if(serverMethodInfo.ParamsInfo.Count != testingParamsInfo.Length)
                    return false;
                for (int i = 0; i < testingParamsInfo.Length; i++)
                {
                    var pI = serverMethodInfo.ParamsInfo[i];
                    var pI2 = testingParamsInfo[i];
                    if( 
                        pI.ParamTypeName != pI2.ParameterType.Name
                    )
                        return false;
                }
            }
            return true;
        }

        public static IEnumerable<MethodInfo> GetPublicMethods(Type type)
        {
            if (type.IsInterface)
            {
                var propertyInfos = new List<MethodInfo>();

                var considered = new List<Type>();
                var queue = new Queue<Type>();
                considered.Add(type);
                queue.Enqueue(type);
                while (queue.Count > 0)
                {
                    var subType = queue.Dequeue();
                    foreach (var subInterface in subType.GetInterfaces())
                    {
                        if (considered.Contains(subInterface)) continue;
                        considered.Add(subInterface);
                        queue.Enqueue(subInterface);
                    }

                    var typeMethods = subType.GetMethods(
                        BindingFlags.Public
                        | BindingFlags.Instance
                        );

                    var newPropertyInfos = typeMethods
                        .Where(x => !propertyInfos.Contains(x));

                    propertyInfos.InsertRange(0, newPropertyInfos);
                }

                return propertyInfos.ToArray();
            }
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        }
        public static JObject GetJsonRpcRequest(
            InvocationInfo info,
            int requestId = 1,
			bool useLowCaseMthdName = false
		)
        {
            var result = new JObject();
            result["jsonrpc"] = "2.0";
			result["method"] = useLowCaseMthdName 
				? info.TargetMethod.Name.ToLower() 
				: info.TargetMethod.Name;
            result["params"] = JArray.FromObject(info.Arguments);
            result["id"] = requestId;
            return result;
        }

        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        /* Task<Result> -> Task<object> */
        public static async Task<object> ResTaskToObjTask<T1>(Task<T1> task)
        {
            return await task.ConfigureAwait(false);
        }
        private static readonly ConcurrentDictionary<Type, MethodInfo> ResTaskToObjTaskMethodInfosCache
            = new ConcurrentDictionary<Type, MethodInfo>();
        public static MethodInfo GetGenericResTaskToObjTaskMethodInfo(Type paramType)
        {
            return ResTaskToObjTaskMethodInfosCache.GetOrAdd(
                paramType,
                parType => 
                    typeof(JsonRpcClientProcessor).GetMethod(
                        MyNameof.GetMethodName(() => ResTaskToObjTask<int>(null)),
                        BindingFlags.Static | BindingFlags.Public
                    ).MakeGenericMethod(parType)
            );
        }

        /* Task<object> -> Task<Result> */
        public static async Task<TResult> ObjTaskToResTask<TResult>(
            Task<object> objTask
        )
        {
            var result = await objTask.ConfigureAwait(false);
            return (TResult)result;
        }
        private static readonly ConcurrentDictionary<Type, MethodInfo> ObjTaskToResTaskMethodInfosCache
            = new ConcurrentDictionary<Type, MethodInfo>();
        public static MethodInfo GetGenericObjTaskToResTaskMethodInfo(Type resultType)
        {
            return ObjTaskToResTaskMethodInfosCache.GetOrAdd(
                resultType,
                resType => typeof(JsonRpcClientProcessor).GetMethod(
                    MyNameof.GetMethodName(() => ObjTaskToResTask<int>(null)),
                    BindingFlags.Static | BindingFlags.Public
                ).MakeGenericMethod(resType)
            );
        }

        /**/
        private static readonly ConcurrentDictionary<Type, MethodInfo> TaskFromResultMethodInfosCache
            = new ConcurrentDictionary<Type, MethodInfo>();
        public static MethodInfo GetGenericTaskFromResultMethodInfo(Type resultType)
        {
            return TaskFromResultMethodInfosCache.GetOrAdd(
                resultType,
                resType => typeof(Task).GetMethod(
                    MyNameof.GetMethodName(() => Task.FromResult<object>(null)),
                    BindingFlags.Static | BindingFlags.Public
                ).MakeGenericMethod(resType)
            );
        }

        public static async Task<object> GetInvokeResultFromImpl<T1>(
            T1 impl,
            InvocationInfo info
        )
        {
            if (info.TargetMethod.ReturnType == typeof (Task))
            {
                var task = (Task)info.TargetMethod.Invoke(impl, info.Arguments);
                await task.ConfigureAwait(false);
                return null;
            }
            if (
                info.TargetMethod.ReturnType.IsGenericType &&
                info.TargetMethod.ReturnType.GetGenericTypeDefinition() == typeof (Task<>)
            )
            {
                var resultTask = info.TargetMethod.Invoke(impl, info.Arguments);
                /**/
                var returnType = info.TargetMethod.ReturnType.GetGenericArguments()[0];
                var genericMthd = JsonRpcClientProcessor.GetGenericResTaskToObjTaskMethodInfo(returnType);
                return await ((Task<object>)(genericMthd.Invoke(null, new[] { resultTask }))).ConfigureAwait(false);
            }
            return info.TargetMethod.Invoke(impl, info.Arguments);
        }

        // invocationImpl returns TRes in Task<TRes>
        public static object DoInvokeHelper(
            InvocationInfo info,
            Func<InvocationInfo, Task<object>> invocationImpl
        )
        {
            /*
            try
            {
             */
                if (
                    info.TargetMethod.ReturnType == typeof(Task)
                )
                {
                    return invocationImpl(info);
                }
                if (
                    info.TargetMethod.ReturnType.IsGenericType &&
                    info.TargetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                )
                {
                    var resultTask = invocationImpl(info);
                    var returnType = info.TargetMethod.ReturnType.GetGenericArguments()[0];
                    var genericMthd = GetGenericObjTaskToResTaskMethodInfo(returnType);
                    return genericMthd.Invoke(null, new object[] { resultTask });
                }
                return invocationImpl(info).Result;
            /*
            }
            catch (AggregateException aExc)
            {
                ExceptionDispatchInfo.Capture(aExc.InnerException).Throw();
                throw new NotImplementedException();
            }*/
        }
		public static bool IsNullOrEmpty(this JToken token)
		{
			return (token == null) ||
				   (token.Type == JTokenType.Array && !token.HasValues) ||
				   (token.Type == JTokenType.Object && !token.HasValues) ||
				   (token.Type == JTokenType.String && token.ToString() == String.Empty) ||
				   (token.Type == JTokenType.Null);
		}
        public static async Task<object> GetJsonRpcResult(JObject answer, InvocationInfo info)
        {
            JToken error;
            if (answer.TryGetValue("error", out error) && !error.IsNullOrEmpty())
            {
                var errorCode = error["code"].Value<int>();
                if (errorCode == -32501)
                {
                    var rethrowableData 
                        = JsonConvert.DeserializeObject<RpcRethrowableExceptionData>(
                            error["message"].ToString()
                        );
                    _log.Error(
                        "RpcExc {2} {0} '{1}'",
                        info.TargetMethod.Name,
                        JsonConvert.SerializeObject(rethrowableData),
                        info.TargetMethod.DeclaringType
                    );
                    if (
                        rethrowableData.ErrorCode
                        == (int)EJsonRpcClientProcessorServiceErrCodes.RetryLaterRpcExcCode
                    )
                    {
                        await Task.Delay(JsonRpcClientProcessorConstants.RetryLaterTimeout).ConfigureAwait(false);
                        throw new RetryLaterCommandFromServerTimeoutException();
                    }
                    throw new RpcRethrowableException(rethrowableData);
                }
				throw new JsonRpcException()
				{
					JsonErrorCode = errorCode,
					JsonErrorMessage = error["message"].ToString()
				};
            }
            JToken funcResult;
            if (!answer.TryGetValue("result", out funcResult))
                throw new Exception("No result in JObject");
            
            if (info.TargetMethod.ReturnType == typeof(void))
            {
                if (
                    !funcResult.ToString().In("success", "null", string.Empty)
                )
                    throw new Exception("Void func not success");
                return null;
            }
            if (info.TargetMethod.ReturnType == typeof(Task))
            {
                if (
                    !funcResult.ToString().In("success", "null", string.Empty)
                )
                    throw new Exception("Void task not success");
                return null;
            }
            
            if (
                info.TargetMethod.ReturnType.IsGenericType &&
                info.TargetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            )
            {
                var resultType = info.TargetMethod.ReturnType.GetGenericArguments()[0];
                return funcResult.ToObject(resultType);
            }
            return funcResult.ToObject(info.TargetMethod.ReturnType);
        }
    }
}
