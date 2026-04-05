#region Related components
using System;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WampSharp.V2.Core.Contracts;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		/// <summary>
		/// Gets the stack trace of the error exception
		/// </summary>
		/// <param name="exception">The exception to get the stack</param>
		/// <param name="onlyStack">true to get only stack trace when the exception is <see cref="WampException">WampException</see></param>
		/// <returns>The string that presents the stack trace</returns>
		public static string GetStack(this Exception exception, bool onlyStack = true, RequestInfo requestInfo = null)
		{
			var stack = string.Empty;
			if (exception is WampException wampException)
			{
				if (wampException.Details != null && wampException.Details.Count == 7)
				{
					stack = wampException.Details["Stack"] as string;
					if (!onlyStack)
					{
						var innerStack = wampException.Details["InnerStack"] as string;
						stack += string.IsNullOrWhiteSpace(innerStack) ? "" : "\r\n" + innerStack;
					}
				}
				else
				{
					var wampDetails = wampException.GetDetails(requestInfo);
					stack = wampDetails.InnerJSON != null
						? (onlyStack ? wampDetails.InnerJSON.Get<string>("Stack") : wampDetails.InnerJSON.ToString(Formatting.Indented))?.Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\")
						: wampDetails.Stack?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
				}
			}
			else
			{
				if (exception is AggregateException agg)
				{
					var counter = 0;
					foreach (var inner in agg.Flatten().InnerExceptions)
					{
						counter++;
						stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n" + $"> Message: {inner.Message}\r\n" + $"> Type: {inner.GetType()}\r\n" + inner.StackTrace;
					}
				}
				else if (!onlyStack)
				{
					stack = exception.StackTrace;
					var inner = onlyStack ? null : exception.InnerException;
					var counter = 0;
					while (inner != null)
					{
						counter++;
						stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n" + $"> Message: {inner.Message}\r\n" + $"> Type: {inner.GetType()}\r\n" + inner.StackTrace;
						inner = inner.InnerException;
					}
				}
			}
			return stack;
		}

		/// <summary>
		/// Gets the details of the WAMP exception
		/// </summary>
		/// <param name="wampException"></param>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static (int Code, string Message, string Type, string Stack, Exception InnerException, JObject InnerJSON) GetDetails(this WampException wampException, RequestInfo requestInfo = null)
		{
			string message = "", type = "", stack = "";
			JObject innerJson = null;

			// unavailable
			if (wampException.ErrorUri.Equals(WampErrors.NoSuchProcedure) || wampException.ErrorUri.Equals(WampErrors.NoSuchRegistration) || wampException.ErrorUri.Equals(WampErrors.CalleeUnregistered))
			{
				if (wampException.Arguments != null && wampException.Arguments.Length > 0 && wampException.Arguments[0] != null && wampException.Arguments[0] is JValue msg)
				{
					message = $"{msg.Value}";
					var start = message.IndexOf("'") + 1;
					var end = message.IndexOf("'", start);
					message = $"The service ({message.Substring(start, end - start).Replace("'", "")}) is unavailable";
				}
				else
					message = "The service is unavailable";

				type = "ServiceUnavailableException";
				stack = wampException.StackTrace;
			}

			// cannot serialize
			else if (wampException.ErrorUri.Equals(WampErrors.InvalidArgument))
			{
				message = "Cannot serialize or deserialize one of arguments, all arguments must be instance of a serializable class - interfaces are not be deserialized";
				if (wampException.Arguments != null && wampException.Arguments.Length > 0 && wampException.Arguments[0] != null && wampException.Arguments[0] is JValue msg)
					message += $" => {msg.Value}";
				type = "SerializationException";
				stack = wampException.StackTrace;
			}

			// runtime error
			else if (wampException.ErrorUri.Equals("wamp.error.runtime_error"))
			{
				if (wampException.Details != null && wampException.Details.Count == 7)
				{
					message = wampException.Details["Message"] as string;
					var innerStack = wampException.Details["InnerStack"] as string;
					stack = $"{wampException.Details["Stack"]}{(string.IsNullOrWhiteSpace(innerStack) ? "" : $"\r\n{innerStack}")}";
					innerJson = wampException.Details["InnerJson"] as JObject;
					type = wampException.Details["Type"] as string ?? "ServiceOperationException";
				}
				else
				{
					var firstArgument = wampException.Arguments?.First();
					var infoJson = firstArgument != null && firstArgument is JObject jinfo ? jinfo : null;
					var infoValue = firstArgument != null && firstArgument is JValue vinfo ? vinfo : null;
					var requestJson = wampException.Arguments != null && wampException.Arguments.Length > 2 && wampException.Arguments[2] != null && wampException.Arguments[2] is JObject jrequest ? jrequest : null;
					var exceptionJson = wampException.Arguments != null && wampException.Arguments.Length > 4 && wampException.Arguments[4] != null && wampException.Arguments[4] is JObject jexception ? jexception : null;

					if (infoJson != null)
						foreach (var info in infoJson)
						{
							var infoVal = info.Value != null && info.Value is JValue jval ? jval : null;
							if (infoVal != null && infoVal.Value != null)
								stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")	+ infoVal.Value.ToString();
						}
					else if (infoValue != null)
						stack = wampException.StackTrace;

					var serviceName = "unknown";
					if (requestInfo != null)
						serviceName = requestInfo.ServiceName;
					else if (requestJson != null)
					{
						var info = requestJson.First;
						if (info != null && info is JProperty jprop && jprop.Name.Equals("RequestInfo") && jprop.Value != null && jprop.Value is JObject jobj)
							serviceName = jobj.As<RequestInfo>()?.ServiceName ?? "unknown";
					}

					innerJson = exceptionJson?.GetJsonException();
					message = innerJson?.Get<string>("Message") ?? infoValue?.Value?.ToString() ?? $"Error occurred at \"services.{serviceName.ToLower()}\"";
					type = innerJson?.Get<JValue>("Type")?.Value?.ToString()?.ToArray('.').Last() ?? "ServiceOperationException";
				}
			}

			// all others
			else
			{
				if (wampException.ErrorUri.Equals(WampErrors.Canceled))
				{
					message = "Operation canceled";
					type = "OperationCanceledException";
				}
				else
				{
					message = wampException.Message;
					type = wampException.GetTypeName(true);
				}
				stack = wampException.StackTrace;
			}

			return (type.GetErrorCode(), message, type, stack, wampException.InnerException, innerJson);
		}

		static int GetErrorCode(this string type)
		{
			switch (type)
			{
				case "MethodNotAllowedException":
					return (int)HttpStatusCode.MethodNotAllowed;

				case "NotImplementedException":
					return (int)HttpStatusCode.NotImplemented;

				case "AccessDeniedException":
					return (int)HttpStatusCode.Forbidden;

				case "UnauthorizedException":
					return (int)HttpStatusCode.Unauthorized;

				default:
					if (type.Contains("Invalid"))
						return (int)HttpStatusCode.BadRequest;
					if (type.Equals("ServiceNotFoundException") || type.Contains("Unavailable"))
						return (int)HttpStatusCode.ServiceUnavailable;
					if (type.EndsWith("NotFoundException"))
						return (int)HttpStatusCode.NotFound;
					return (int)HttpStatusCode.InternalServerError;
			}
		}

		static JObject GetJsonException(this JToken exception)
		{
			var json = new JObject
			{
				{ "Message", exception["Message"] },
				{ "Type", exception["ClassName"] },
				{ "StackTrace", exception["StackTraceString"] },
				{ "Method", exception["ExceptionMethod"] },
				{ "Source", exception["Source"] }
			};

			if (exception["InnerException"] is JToken inner)
				json["InnerException"] = inner.GetJsonException();

			return json;
		}

		/// <summary>
		/// Gets the runtime exception to throw
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="exception"></param>
		/// <param name="message"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static WampException GetRuntimeException(this RequestInfo requestInfo, Exception exception, string message = null, Action<string, Exception> onCompleted = null)
		{
			// normalize exception
			exception = exception is RepositoryOperationException
				? exception.InnerException
				: exception;

			// prepare message
			message = exception != null
				? string.IsNullOrWhiteSpace(message) ? exception.Message : $"{message} => {exception.Message}"
				: message ?? "Error occurred while processing";

			// pre-process
			onCompleted?.Invoke(message, exception);

			// return the exception
			Dictionary<string, object> errorDetails;
			var errorURI = "wamp.error.runtime_error";

			if (exception is WampException wampException)
			{
				if (wampException.ErrorUri.Equals("wamp.error.runtime_error") && wampException.Details != null && wampException.Details.Count == 7)
					return wampException;

				var wampDetails = wampException.GetDetails(requestInfo);
				var innerStack = "";
				var innerException = wampException?.InnerException;
				var counter = 0;
				while (innerException != null)
				{
					counter++;
					innerStack += (innerStack != "" ? "\r\n" : "") + $"--- Inner ({counter}): ---------------------- \r\n{innerException.StackTrace}";
					innerException = innerException.InnerException;
				}
				errorDetails = new Dictionary<string, object>
				{
					["Code"] = wampDetails.Code,
					["Message"] = wampDetails.Message,
					["Type"] = wampDetails.Type,
					["Stack"] = wampDetails.Stack,
					["InnerStack"] = innerStack,
					["InnerJson"] = wampDetails.InnerJSON,
					["RequestInfo"] = requestInfo.ToJson()
				};
				errorURI = wampException.ErrorUri;
			}

			else
				errorDetails = new Dictionary<string, object>
				{
					["Code"] = (exception?.GetTypeName(true) ?? "").GetErrorCode(),
					["Message"] = message,
					["Type"] = exception?.GetTypeName(true) ?? "ServiceOperationException",
					["Stack"] = exception is AggregateException agg ? agg.GetStack() : exception?.StackTrace,
					["InnerStack"] = exception?.GetStack(false, requestInfo),
					["InnerJson"] = null,
					["RequestInfo"] = requestInfo.ToJson()
				};

			return new WampException(errorDetails, errorURI, Array.Empty<object>());
		}
	}
}