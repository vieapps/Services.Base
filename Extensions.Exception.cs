#region Related components
using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using WampSharp.V2.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
		public static string GetStack(this Exception exception, bool onlyStack = true)
		{
			var stack = "";
			if (exception != null && exception is WampException wampException)
			{
				var details = wampException.GetDetails();
				stack = details.Item6 != null
					? (onlyStack ? details.Item6.Get<string>("Stack") : details.Item6.ToString(Formatting.Indented))?.Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\")
					: details.Item4?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
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
			var code = (int)HttpStatusCode.InternalServerError;
			var message = "";
			var type = "";
			var stack = "";
			Exception inner = null;
			JObject jsonException = null;

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
				inner = wampException;

				if (wampException.Arguments != null && wampException.Arguments.Length > 0 && wampException.Arguments[0] != null && wampException.Arguments[0] is JObject)
					foreach (var info in wampException.Arguments[0] as JObject)
					{
						if (info.Value != null && info.Value is JValue && (info.Value as JValue).Value != null)
							stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
								+ (info.Value as JValue).Value.ToString();
					}

				if (requestInfo == null && wampException.Arguments != null && wampException.Arguments.Length > 2 && wampException.Arguments[2] != null && wampException.Arguments[2] is JObject)
				{
					var info = (wampException.Arguments[2] as JObject).First;
					if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
						requestInfo = (info as JProperty).Value.FromJson<RequestInfo>();
				}

				jsonException = wampException.Arguments != null && wampException.Arguments.Length > 4 && wampException.Arguments[4] != null && wampException.Arguments[4] is JObject
					? (wampException.Arguments[4] as JObject).GetJsonException()
					: null;

				message = jsonException != null
					? jsonException.Get<string>("Message")
					: $"Error occurred at \"services.{(requestInfo?.ServiceName ?? "unknown").ToLower()}\"";

				type = jsonException != null && jsonException["Type"] != null
					? jsonException.Get<JValue>("Type")?.Value?.ToString()?.ToArray('.').Last() ?? "ServiceOperationException"
					: "ServiceOperationException";
			}

			// unknown
			else
			{
				message = wampException.Message;
				type = wampException.GetTypeName(true);
				stack = wampException.StackTrace;
				inner = wampException.InnerException;
			}

			// status code
			switch (type)
			{
				case "FileNotFoundException":
				case "InformationNotFoundException":
					code = (int)HttpStatusCode.NotFound;
					break;

				case "MethodNotAllowedException":
					code = (int)HttpStatusCode.MethodNotAllowed;
					break;

				case "NotImplementedException":
					code = (int)HttpStatusCode.NotImplemented;
					break;

				case "AccessDeniedException":
					code = (int)HttpStatusCode.Forbidden;
					break;

				case "UnauthorizedException":
					code = (int)HttpStatusCode.Unauthorized;
					break;

				default:
					if (type.Contains("Invalid"))
						code = (int)HttpStatusCode.BadRequest;
					else if (type.Equals("ServiceNotFoundException") || type.Contains("Unavailable"))
						code = (int)HttpStatusCode.ServiceUnavailable;
					break;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(code, message, type, stack, inner, jsonException);
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
	}
}