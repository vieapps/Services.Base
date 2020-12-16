#region Related components
using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
			var stack = "";
			if (exception != null && exception is WampException wampException)
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
					stack = wampDetails.Item6 != null
						? (onlyStack ? wampDetails.Item6.Get<string>("Stack") : wampDetails.Item6.ToString(Formatting.Indented))?.Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\")
						: wampDetails.Item4?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
				}
			}
			else if (exception != null)
			{
				stack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n"
						+ "> Message: " + inner.Message + "\r\n"
						+ "> Type: " + inner.GetType().ToString() + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
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
		public static Tuple<int, string, string, string, Exception, JObject> GetDetails(this WampException wampException, RequestInfo requestInfo = null)
		{
			var message = "";
			var type = "";
			var stack = "";
			JObject innerJson = null;

			// unavailable
			if (wampException.ErrorUri.Equals("wamp.error.no_such_procedure") || wampException.ErrorUri.Equals("wamp.error.no_such_registration") || wampException.ErrorUri.Equals("wamp.error.callee_unregistered"))
			{
				if (wampException.Arguments != null && wampException.Arguments.Length > 0 && wampException.Arguments[0] != null && wampException.Arguments[0] is JValue)
				{
					message = (wampException.Arguments[0] as JValue).Value.ToString();
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
			else if (wampException.ErrorUri.Equals("wamp.error.invalid_argument"))
			{
				message = "Cannot serialize or deserialize one of arguments, all arguments must be instance of a serializable class - interfaces are not be deserialized";
				if (wampException.Arguments != null && wampException.Arguments.Length > 0 && wampException.Arguments[0] != null && wampException.Arguments[0] is JValue)
					message += $" => {(wampException.Arguments[0] as JValue).Value}";
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
				}
				else
				{
					var firstArgument = wampException.Arguments?.First();
					var infoJson = firstArgument != null && firstArgument is JObject ? firstArgument as JObject : null;
					var infoValue = firstArgument != null && firstArgument is JValue ? firstArgument as JValue : null;
					var requestJson = wampException.Arguments != null && wampException.Arguments.Length > 2 && wampException.Arguments[2] != null && wampException.Arguments[2] is JObject ? wampException.Arguments[2] as JObject : null;
					var exceptionJson = wampException.Arguments != null && wampException.Arguments.Length > 4 && wampException.Arguments[4] != null && wampException.Arguments[4] is JObject ? wampException.Arguments[4] as JObject : null;

					if (infoJson != null)
						foreach (var info in infoJson)
						{
							var infoVal = info.Value != null && info.Value is JValue ? info.Value as JValue : null;
							if (infoVal != null && infoVal.Value != null)
								stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
									+ infoVal.Value.ToString();
						}
					else if (infoValue != null)
						stack = wampException.StackTrace;

					var serviceName = "unknown";
					if (requestInfo != null)
						serviceName = requestInfo.ServiceName;
					else if (requestJson != null)
					{
						var info = requestJson.First;
						if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
							serviceName = (info as JProperty).Value.FromJson<RequestInfo>()?.ServiceName ?? "unknown";
					}

					innerJson = exceptionJson?.GetJsonException();
					message = innerJson?.Get<string>("Message") ?? infoValue?.Value?.ToString() ?? $"Error occurred at \"services.{serviceName.ToLower()}\"";
					type = innerJson?.Get<JValue>("Type")?.Value?.ToString()?.ToArray('.').Last() ?? "ServiceOperationException";
				}
			}

			// unknown
			else
			{
				message = wampException.Message;
				type = wampException.GetTypeName(true);
				stack = wampException.StackTrace;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(type.GetErrorCode(), message, type, stack, wampException.InnerException, innerJson);
		}

		static int GetErrorCode(this string type)
		{
			switch (type)
			{
				case "FileNotFoundException":
				case "InformationNotFoundException":
					return (int)HttpStatusCode.NotFound;

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
					else if (type.Equals("ServiceNotFoundException") || type.Contains("Unavailable"))
						return (int)HttpStatusCode.ServiceUnavailable;
					else if (type.EndsWith("NotFoundException"))
						return (int)HttpStatusCode.NotFound;
					else
						return (int)HttpStatusCode.InternalServerError;
			}
		}

		static JObject GetJsonException(this JToken exception)
		{
			var json = new JObject
			{
				{ "Message", exception["Message"] },
				{ "Type", exception["ClassName"] },
				{ "Method", exception["ExceptionMethod"] },
				{ "Source", exception["Source"] },
				{ "Stack", exception["StackTraceString"] },
			};

			var inner = exception["InnerException"];
			if (inner != null && inner is JObject)
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
			exception = exception != null && exception is RepositoryOperationException
				? exception.InnerException
				: exception;

			// prepare message
			message = string.IsNullOrWhiteSpace(message)
				? exception != null
					? exception.Message
					: $"Error occurred while processing"
				: message;

			// pre-process
			onCompleted?.Invoke(message, exception);

			// return the exception
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
				var details = new Dictionary<string, object>
				{
					["Code"] = wampDetails.Item1,
					["Message"] = wampDetails.Item2,
					["Type"] = wampDetails.Item3,
					["Stack"] = wampDetails.Item4,
					["InnerStack"] = innerStack,
					["InnerJson"] = wampDetails.Item6,
					["RequestInfo"] = requestInfo.ToJson()
				};
				return new WampException(details, wampException.ErrorUri, new object[0]);
			}

			else
			{
				var innerStack = "";
				var innerException = exception?.InnerException;
				var counter = 0;
				while (innerException != null)
				{
					counter++;
					innerStack += (innerStack != "" ? "\r\n" : "") + $"--- Inner ({counter}): ---------------------- \r\n{innerException.StackTrace}";
					innerException = innerException.InnerException;
				}
				var details = new Dictionary<string, object>
				{
					["Code"] = (exception?.GetTypeName(true) ?? "").GetErrorCode(),
					["Message"] = message,
					["Type"] = exception?.GetTypeName(true) ?? "ServiceOperationException",
					["Stack"] = exception?.StackTrace,
					["InnerStack"] = innerStack,
					["InnerJson"] = null,
					["RequestInfo"] = requestInfo.ToJson()
				};
				return new WampException(details, "wamp.error.runtime_error", new object[0]);
			}
		}
	}
}