#region Related components
using System;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Dynamic;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using WampSharp.V2.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
	public static partial class Extensions
	{

		#region Filter
		static FilterBy<T> GetFilterBy<T>(string attribute, string @operator, JValue value) where T : class
		{
			return new FilterBy<T>(new JObject()
			{
				{ "Attribute", attribute },
				{ "Operator", @operator },
				{ "Value", value }
			});
		}

		static FilterBys<T> GetFilterBys<T>(string @operator, JObject children) where T : class
		{
			var childFilters = new List<IFilterBy<T>>();
			foreach (var info in children)
			{
				IFilterBy<T> filter = null;
				var name = info.Key;
				var value = info.Value;

				// child expressions
				if (value is JObject && (name.IsEquals("And") || name.IsEquals("Or")))
					filter = Extensions.GetFilterBys<T>(name, value as JObject);

				// special comparisons
				else if (value is JValue)
				{
					var op = (value as JValue).Value.ToString();
					if (op.IsEquals("IsNull") || op.IsEquals("IsNotNull") || op.IsEquals("IsEmpty") || op.IsEquals("IsNotEmpty"))
						filter = Extensions.GetFilterBy<T>(name, op, null);
				}

				// normal comparison
				else if (value is JObject)
				{
					var prop = (value as JObject).Properties().FirstOrDefault();
					if (prop != null && prop.Value != null && prop.Value is JValue && (prop.Value as JValue).Value != null)
						filter = Extensions.GetFilterBy<T>(name, prop.Name, prop.Value as JValue);
				}

				if (filter != null)
					childFilters.Add(filter);
			}
			return new FilterBys<T>(@operator.ToEnum<GroupOperator>(), childFilters);
		}

		/// <summary>
		/// Converts the JSON object to filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this JObject filterby) where T : class
		{
			var orFilters = filterby["Or"] as JObject;
			var andFilters = filterby["And"] as JObject;
			var rootFilters = orFilters != null
				? orFilters
				: andFilters != null
					? andFilters
					: filterby;

			var filters = orFilters != null
				? Filters<T>.Or()
				: Filters<T>.And();

			foreach (var info in rootFilters)
				if (!info.Key.Equals("") && !info.Key.IsEquals("Query"))
				{
					IFilterBy<T> filter = null;
					var name = info.Key;
					var value = info.Value;

					// child expressions
					if (value is JObject && (name.IsEquals("And") || name.IsEquals("Or")))
						filter = Extensions.GetFilterBys<T>(name, value as JObject);

					// special comparisions
					else if (value is JValue)
					{
						var op = (value as JValue).Value.ToString();
						if (op.IsEquals("IsNull") || op.IsEquals("IsNotNull") || op.IsEquals("IsEmpty") || op.IsEquals("IsNotEmpty"))
							filter = Extensions.GetFilterBy<T>(name, op, null);
					}

					// normal comparisions
					else if (value is JObject)
					{
						var prop = (value as JObject).Properties().FirstOrDefault();
						if (prop != null && prop.Value != null && prop.Value is JValue && (prop.Value as JValue).Value != null)
							filter = Extensions.GetFilterBy<T>(name, prop.Name, prop.Value as JValue);
					}

					if (filter != null)
						filters.Add(filter);
				}

			return filters != null && filters.Children.Count > 0
				? filters
				: null;
		}

		/// <summary>
		/// Converts the Expando object to filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this ExpandoObject filterby) where T : class
		{
			return Extensions.ToFilterBy<T>(JObject.FromObject(filterby));
		}

		static JProperty AddClientJson(JObject serverJson)
		{
			var @operator = (serverJson["Operator"] as JValue).Value.ToString();
			if (serverJson["Children"] == null)
			{
				var token = @operator.IsEquals("IsNull") || @operator.IsEquals("IsNotNull") || @operator.IsEquals("IsEmpty") || @operator.IsEquals("IsNotEmpty")
					? new JValue(@operator) as JToken
					: new JObject()
					{
						{
							@operator,
							new JValue((serverJson["Value"] as JValue)?.Value)
						}
					} as JToken;
				return new JProperty((serverJson["Attribute"] as JValue).Value.ToString(), token);
			}
			else
			{
				var children = serverJson["Children"] as JArray;
				if (children == null || children.Count < 1)
					return null;

				var clientJson = new JObject();
				foreach (JObject childJson in children)
					clientJson.Add(Extensions.AddClientJson(childJson));
				return new JProperty(@operator, clientJson);
			}
		}

		/// <summary>
		/// Converts the filtering expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <param name="query"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this IFilterBy<T> filterby, string query = null) where T : class
		{
			var clientJson = new JObject();
			if (!string.IsNullOrEmpty(query))
				clientJson.Add(new JProperty("Query", query));

			var json = Extensions.AddClientJson(filterby.ToJson());
			if (json != null)
				clientJson.Add(json);

			return clientJson;
		}

		/// <summary>
		/// Gets MD5 hash
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static string GetMD5<T>(this IFilterBy<T> filterby) where T : class
		{
			return filterby != null
				? filterby.ToClientJson().ToString(Formatting.None).ToLower().GetMD5()
				: "";
		}
		#endregion

		#region Sort
		/// <summary>
		/// Converts the JSON object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this JObject sortby) where T : class
		{
			SortBy<T> sort = null;
			foreach (var info in sortby)
				if (!info.Key.Equals(""))
				{
					var attribute = info.Key;
					var mode = (info.Value as JValue).Value.ToString().ToEnum<SortMode>();

					sort = sort != null
						? mode.Equals(SortMode.Ascending)
							? sort.ThenByAscending(attribute)
							: sort.ThenByDescending(attribute)
						: mode.Equals(SortMode.Ascending)
							? Sorts<T>.Ascending(attribute)
							: Sorts<T>.Descending(attribute);
				}
			return sort;
		}

		/// <summary>
		/// Converts the Expando object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this ExpandoObject sortby) where T : class
		{
			return Extensions.ToSortBy<T>(JObject.FromObject(sortby));
		}

		static void AddClientJson(JObject clientJson, JObject serverJson)
		{
			var attribute = (serverJson["Attribute"] as JValue).Value.ToString();
			var mode = (serverJson["Mode"] as JValue).Value.ToString();
			clientJson.Add(new JProperty(attribute, mode));

			var thenby = serverJson["ThenBy"];
			if (thenby != null && thenby is JObject)
				Extensions.AddClientJson(clientJson, thenby as JObject);
		}

		/// <summary>
		/// Converts the sorting expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this SortBy<T> sortby) where T : class
		{
			var clientJson = new JObject();
			Extensions.AddClientJson(clientJson, sortby.ToJson());
			return clientJson;
		}

		/// <summary>
		/// Gets MD5 hash
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static string GetMD5<T>(this SortBy<T> sortby) where T : class
		{
			return sortby != null
				? sortby.ToClientJson().ToString(Formatting.None).ToLower().GetMD5()
				: "";
		}
		#endregion

		#region Pagination
		/// <summary>
		/// Computes the total of pages from total of records and page size
		/// </summary>
		/// <param name="totalRecords"></param>
		/// <param name="pageSize"></param>
		/// <returns></returns>
		public static int GetTotalPages(long totalRecords, int pageSize)
		{
			var totalPages = (int)(totalRecords / pageSize);
			if (totalRecords - (totalPages * pageSize) > 0)
				totalPages += 1;
			return totalPages;
		}

		/// <summary>
		/// Computes the total of pages from total of records and page size
		/// </summary>
		/// <param name="info"></param>
		/// <returns></returns>
		public static int GetTotalPages(this Tuple<long, int> info)
		{
			return Extensions.GetTotalPages(info.Item1, info.Item2);
		}

		/// <summary>
		/// Gets the tuple of pagination from this JSON (1st element is total records, 2nd element is total pages, 3rd element is page size, 4th element is page number)
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static Tuple<long, int, int, int> GetPagination(this JObject pagination)
		{
			var totalRecords = pagination["TotalRecords"] != null && pagination["TotalRecords"] is JValue && (pagination["TotalRecords"] as JValue).Value != null
				? (pagination["TotalRecords"] as JValue).Value.CastAs<long>()
				: -1;

			var pageSize = pagination["PageSize"] != null && pagination["PageSize"] is JValue && (pagination["PageSize"] as JValue).Value != null
				? (pagination["PageSize"] as JValue).Value.CastAs<int>()
				: 20;
			if (pageSize < 0)
				pageSize = 20;

			var totalPages = pagination["TotalPages"] != null && pagination["TotalPages"] is JValue && (pagination["TotalPages"] as JValue).Value != null
				? (pagination["TotalPages"] as JValue).Value.CastAs<int>()
				: -1;
			if (totalPages < 0)
				totalPages = Extensions.GetTotalPages(totalRecords, pageSize);

			var pageNumber = pagination["PageNumber"] != null && pagination["PageNumber"] is JValue && (pagination["PageNumber"] as JValue).Value != null
				? (pagination["PageNumber"] as JValue).Value.CastAs<int>()
				: 20;
			if (pageNumber < 1)
				pageNumber = 1;
			else if (totalPages > 0 && pageNumber > totalPages)
				pageNumber = totalPages;

			return new Tuple<long, int, int, int>(totalRecords, totalPages, pageSize, pageNumber);
		}

		/// <summary>
		/// Gets the tuple of pagination from this object (1st element is total records, 2nd element is total pages, 3rd element is page size, 4th element is page number)
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static Tuple<long, int, int, int> GetPagination(this ExpandoObject pagination)
		{
			var totalRecords = pagination.Get<long>("TotalRecords", -1);

			var pageSize = pagination.Get<int>("PageSize", 20);
			pageSize = pageSize < 0 ? 10 : pageSize;

			var totalPages = pagination.Get<int>("TotalPages", -1);
			totalPages = totalPages < 0
				? totalRecords > 0
					? Extensions.GetTotalPages(totalRecords, pageSize)
					: 0
				: totalPages;

			var pageNumber = pagination.Get<int>("PageNumber", 1);
			pageNumber = pageNumber < 1
				? 1
				: totalPages > 0 && pageNumber > totalPages
					? totalPages
					: pageNumber;

			return new Tuple<long, int, int, int>(totalRecords, totalPages, pageSize, pageNumber);
		}

		/// <summary>
		/// Gets the pagination JSON
		/// </summary>
		/// <param name="totalRecords"></param>
		/// <param name="totalPages"></param>
		/// <param name="pageSize"></param>
		/// <param name="pageNumber"></param>
		/// <returns></returns>
		public static JObject GetPagination(long totalRecords, int totalPages, int pageSize, int pageNumber)
		{
			return new JObject()
			{
				{ "TotalRecords", totalRecords },
				{ "TotalPages", totalPages},
				{ "PageSize", pageSize },
				{ "PageNumber", pageNumber }
			};
		}

		/// <summary>
		/// Gets the pagination JSON
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static JObject GetPagination(this Tuple<long, int, int, int> pagination)
		{
			return Extensions.GetPagination(pagination.Item1, pagination.Item2, pagination.Item3, pagination.Item4);
		}
		#endregion

		#region Exceptions
		/// <summary>
		/// Gets the stack trace of this error exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static string GetStack(this Exception exception)
		{
			var stack = "";
			if (exception != null && exception is WampException)
			{
				var details = (exception as WampException).GetDetails();
				stack = details.Item4?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
				if (details.Item6 != null)
					stack = details.Item6.ToString(Formatting.Indented).Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\");
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
		/// Gets the details of a WAMP exception
		/// </summary>
		/// <param name="exception"></param>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Tuple<int, string, string, string, Exception, JObject> GetDetails(this WampException exception, RequestInfo requestInfo = null)
		{
			var code = 500;
			var message = "";
			var type = "";
			var stack = "";
			Exception inner = null;
			JObject jsonException = null;

			// unavailable
			if (exception.ErrorUri.Equals("wamp.error.no_such_procedure") || exception.ErrorUri.Equals("wamp.error.callee_unregistered"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
				{
					message = (exception.Arguments[0] as JValue).Value.ToString();
					var start = message.IndexOf("'");
					var end = message.IndexOf("'", start + 1);
					message = $"The requested service ({message.Substring(start + 1, end - start - 1).Replace("'", "")}) is unavailable";
				}
				else
					message = "The requested service is unavailable";

				type = "ServiceUnavailableException";
				stack = exception.StackTrace;
				code = 503;
			}

			// cannot serialize
			else if (exception.ErrorUri.Equals("wamp.error.invalid_argument"))
			{
				message = "Cannot serialize or deserialize one of argument objects (or child object)";
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
					message += $" => {(exception.Arguments[0] as JValue).Value}";
				type = "SerializationException";
				stack = exception.StackTrace;
			}

			// runtime error
			else if (exception.ErrorUri.Equals("wamp.error.runtime_error"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JObject)
					foreach (var info in exception.Arguments[0] as JObject)
					{
						if (info.Value != null && info.Value is JValue && (info.Value as JValue).Value != null)
							stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
								+ (info.Value as JValue).Value.ToString();
					}

				if (requestInfo == null && exception.Arguments != null && exception.Arguments.Length > 2 && exception.Arguments[2] != null && exception.Arguments[2] is JObject)
				{
					var info = (exception.Arguments[2] as JObject).First;
					if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
						requestInfo = ((info as JProperty).Value as JToken).FromJson<RequestInfo>();
				}

				jsonException = exception.Arguments != null && exception.Arguments.Length > 4 && exception.Arguments[4] != null && exception.Arguments[4] is JObject
					? Extensions.GetJsonException(exception.Arguments[4] as JObject)
					: null;

				message = jsonException != null
					? (jsonException["Message"] as JValue).Value.ToString()
					: $"Error occurred at \"net.vieapps.services.{(requestInfo != null ? requestInfo.ServiceName.ToLower() : "unknown")}\"";

				type = jsonException != null
					? (jsonException["Type"] as JValue).Value.ToString().ToArray('.').Last()
					: "ServiceOperationException";

				inner = exception;
			}

			// unknown
			else
			{
				message = exception.Message;
				type = exception.GetType().GetTypeName(true);
				stack = exception.StackTrace;
				inner = exception.InnerException;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(code, message, type, stack, inner, jsonException);
		}

		static JObject GetJsonException(JObject exception)
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
				json.Add(new JProperty("InnerException", Extensions.GetJsonException(inner as JObject)));

			return json;
		}
		#endregion

		#region Location
		/// <summary>
		/// Gets the current location (IP-based)
		/// </summary>
		public static string CurrentLocation { get; private set; } = "Unknown";

		/// <summary>
		/// Gets the location of the session (IP-based)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="correlationID"></param>
		/// <returns></returns>
		public static async Task<string> GetLocationAsync(this Session session, string correlationID = null)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			try
			{
				var json = await WAMPConnections.CallServiceAsync(new RequestInfo(session, "IPLocations", "IP-Location")
				{
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						{ "ip-address", session.IP }
					},
					CorrelationID = correlationID
				}).ConfigureAwait(false);

				var city = json.Get<string>("City");
				var region = json.Get<string>("Region");
				if (region.Equals(city) && !"N/A".IsEquals(city))
					region = "";
				var country = json.Get<string>("Country");

				if ("N/A".IsEquals(city) && "N/A".IsEquals(region) && "N/A".IsEquals(country))
				{
					if ("Unknown".IsEquals(Extensions.CurrentLocation))
					{
						json = await WAMPConnections.CallServiceAsync(new RequestInfo(session, "IPLocations", "Current")
						{
							CorrelationID = correlationID
						}).ConfigureAwait(false);
						city = json.Get<string>("City");
						region = json.Get<string>("Region");
						if (region.Equals(city) && !"N/A".IsEquals(city))
							region = "";
						country = json.Get<string>("Country");
						Extensions.CurrentLocation = $"{city}, {region}, {country}".Replace(", ,", ",");
					}
					return Extensions.CurrentLocation;
				}

				return $"{city}, {region}, {country}".Replace(", ,", ",");
			}
			catch
			{
				return "Unknown";
			}
		}

		/// <summary>
		/// Gets the location of the request (IP-based)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Task<string> GetLocationAsync(this RequestInfo requestInfo)
			=> requestInfo.Session?.GetLocationAsync(requestInfo.CorrelationID) ?? Task.FromResult("Unknown");
		#endregion

		#region Encryption
		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, byte[] seeds = null, IDictionary<object, object> storage = null)
			=> storage != null
				? storage.ContainsKey("EncryptionKey")
					? storage["EncryptionKey"] as byte[]
					: (storage["EncryptionKey"] = session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256)) as byte[]
				: session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256);

		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, string seeds, IDictionary<object, object> storage)
			=> session.GetEncryptionKey((seeds ?? CryptoService.DEFAULT_PASS_PHRASE).ToBytes(), storage);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, byte[] seeds = null, IDictionary<object, object> storage = null)
			=> storage != null
				? storage.ContainsKey("EncryptionIV")
					? storage["EncryptionIV"] as byte[]
					: (storage["EncryptionIV"] = session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128)) as byte[]
				: session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, string seeds, IDictionary<object, object> storage)
			=> session.GetEncryptionIV((seeds ?? CryptoService.DEFAULT_PASS_PHRASE).ToBytes(), storage);

		/// <summary>
		/// Encrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetEncryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Encrypt(session.GetEncryptionKey(keySeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null), session.GetEncryptionIV(ivSeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null)).ToHex()
				: null;

		/// <summary>
		/// Decrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetDecryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Decrypt(session.GetEncryptionKey(keySeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null), session.GetEncryptionIV(ivSeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null)).ToHex()
				: null;
		#endregion

		#region Get unique name & end-point
		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="user">The string that presents name of the user who runs the process of the business service</param>
		/// <param name="host">The string that presents name of the host that runs the process of the business service</param>
		/// <param name="platform">The string that presents name of the platform that runs the process of the business service</param>
		/// <param name="os">The string that presents name of the operating system that runs the process of the business service</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string user, string host, string platform, string os)
		{
			name = (name ?? "unknown").Trim().ToLower();
			user = (user ?? Environment.UserName).Trim().ToLower();
			host = (host ?? Environment.MachineName).Trim().ToLower();
			platform = (platform ?? RuntimeInformation.FrameworkDescription).Trim();
			os = (os ?? $"{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "macOS")} {RuntimeInformation.OSArchitecture} ({(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Intel Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})").Trim();
			return $"{name}.{user}-{host}-" + $"{platform} @ {os}".GenerateUUID();
		}

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="args">The starting arguments</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string[] args = null)
		{
			var user = args?.FirstOrDefault(a => a.IsStartsWith("/run-user:"));
			var host = args?.FirstOrDefault(a => a.IsStartsWith("/run-host:"));
			var platform = args?.FirstOrDefault(a => a.IsStartsWith("/run-platform:"));
			var os = args?.FirstOrDefault(a => a.IsStartsWith("/run-os:"));
			return Extensions.GetUniqueName(name, user?.Replace(StringComparison.OrdinalIgnoreCase, "/run-user:", "").Trim().UrlDecode(), host?.Replace(StringComparison.OrdinalIgnoreCase, "/run-host:", "").Trim().UrlDecode(), platform?.Replace(StringComparison.OrdinalIgnoreCase, "/run-platform:", "").Trim().UrlDecode(), os?.Replace(StringComparison.OrdinalIgnoreCase, "/run-os:", "").Trim().UrlDecode());
		}

		/// <summary>
		/// Gets the resolved URI with IP address and port
		/// </summary>
		/// <param name="uri"></param>
		/// <returns></returns>
		public static string GetResolvedURI(this Uri uri)
		{
			var host = "";
			if (!IPAddress.TryParse(uri.Host, out IPAddress address))
			{
				address = Dns.GetHostAddresses(uri.Host).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
				host = address == null
					? $" => Could not resolve host \"{host}\""
					: $" => {uri.Scheme}://{new IPEndPoint(address, uri.Port)}{uri.PathAndQuery}";
			}
			return $"{uri}{host}";
		}
		#endregion

	}
}