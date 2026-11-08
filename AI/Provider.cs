#region Related components
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Security.Authentication;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.AI
{
	/// <summary>
	/// Adapts one provider attempt to the common message contracts
	/// </summary>
	/// <remarks>Provider selection, logical retry, business authorization and observability persistence remain outside the adapter.</remarks>
	public abstract class Provider : IDisposable, IAsyncDisposable
	{
		#region Provider contract and lifecycle
		/// <summary>
		/// Gets the provider-profile configuration assigned during registry initialization
		/// </summary>
		public ProviderInfo Info { get; private set; }

		/// <summary>
		/// Assigns the configuration of a newly created provider instance
		/// </summary>
		/// <param name="info">The registry-owned configuration snapshot</param>
		/// <remarks>Initialization precedes publication. The registry replaces active instances instead of reinitializing them.</remarks>
		internal virtual void Initialize(ProviderInfo info)
			=> this.Info = info;

		/// <summary>
		/// Determines whether this adapter supports a logical runtime capability
		/// </summary>
		/// <param name="capability">The logical capability name, such as structured-output or remote-mcp</param>
		/// <returns>true when supported; otherwise false</returns>
		/// <remarks>Adapter support does not guarantee that every configured model supports the feature.</remarks>
		public abstract bool Supports(string capability);

		/// <summary>
		/// Adapts and executes one provider attempt using the common message contracts
		/// </summary>
		/// <param name="request">The effective request with materialized schema and execution-owned MCP metadata</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <returns>The normalized provider response and available per-attempt usage</returns>
		/// <remarks>Logical retries, provider selection and business authorization belong to the execution or business layers.</remarks>
		public abstract Task<ResponseMessage> ExecuteAsync(RequestMessage request, CancellationToken cancellationToken);

		/// <summary>
		/// Releases resources owned by this provider instance
		/// </summary>
		/// <returns>The asynchronous cleanup operation; the base implementation completes immediately</returns>
		/// <remarks>The base provider retains no transport resource. Derived implementations release any resources they own.</remarks>
		public virtual ValueTask DisposeAsync()
			=> default;

		/// <summary>
		/// Runs asynchronous provider cleanup synchronously through the existing Utility helper
		/// </summary>
		public void Dispose()
			=> this.DisposeAsync().Execute(true);
		#endregion

		#region Provider request, transport and response helpers
		/// <summary>
		/// Identifies execution-owned option keys excluded from native provider payloads
		/// </summary>
		internal static readonly HashSet<string> ExecutionOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"model", "timeout", "request-timeout", "mcp-tools", "mcp-endpoint", "mcp-authorization", "mcp-server-label",
			"anthropic-version", "anthropic-beta"
		};

		/// <summary>
		/// Validates the profile identity, credential and HTTP base endpoint used by reference adapters
		/// </summary>
		/// <param name="info">The new configuration to validate</param>
		/// <exception cref="ProviderException">The profile is missing required values or has an invalid endpoint</exception>
		protected static void ValidateConfiguration(ProviderInfo info)
		{
			if (info == null || string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Credential)
				|| !Uri.TryCreate(info.Endpoint, UriKind.Absolute, out var endpoint)
				|| (!endpoint.Scheme.IsEquals(Uri.UriSchemeHttp) && !endpoint.Scheme.IsEquals(Uri.UriSchemeHttps))
				|| !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
				throw new ProviderException("AI provider requires a profile name, credential and HTTP base endpoint") { Code = "invalid-configuration" };
		}

		/// <summary>
		/// Reads an effective request option using case-insensitive key matching
		/// </summary>
		/// <param name="request">The effective request</param>
		/// <param name="name">The option key</param>
		/// <returns>The first matching token, or null when absent</returns>
		protected static JToken GetOption(RequestMessage request, string name)
			=> request.Options?.Properties().FirstOrDefault(property => name.IsEquals(property.Name))?.Value;

		/// <summary>
		/// Resolves the explicit effective model and checks request preparation
		/// </summary>
		/// <param name="request">The effective request with its schema factory already materialized</param>
		/// <returns>The nonempty model from request options or the provider profile</returns>
		/// <exception cref="ArgumentNullException">The request is null</exception>
		/// <exception cref="ProviderException">The provider is uninitialized, the model is invalid or schema materialization is missing</exception>
		protected string GetModel(RequestMessage request)
		{
			if (request == null)
				throw new ArgumentNullException(nameof(request));
			if (this.Info == null)
				throw new ProviderException("AI provider is not initialized") { Code = "invalid-configuration" };
			if (request.Schema == null && request.SchemaFactory != null)
				throw new ProviderException("SchemaFactory must be materialized by the execution layer") { Code = "invalid-request" };
			var model = GetOption(request, "model") ?? new JValue(this.Info.Model);
			if (model.Type != JTokenType.String || string.IsNullOrWhiteSpace(model.Value<string>()))
				throw new ProviderException("AI execution requires an explicit model") { Code = "invalid-request" };
			return model.Value<string>();
		}

		/// <summary>
		/// Clones native provider options while excluding execution-owned settings
		/// </summary>
		/// <param name="request">The effective request containing native options</param>
		/// <returns>The native payload option bag</returns>
		/// <remarks>Raw tool bags and unimplemented streaming/background modes are rejected rather than bypassing execution policy.</remarks>
		/// <exception cref="UnsupportedCapabilityException">The options request an unsupported execution mode or raw tool configuration</exception>
		protected static JObject GetPayloadOptions(RequestMessage request)
		{
			var payload = new JObject();
			if (request.Options != null)
				foreach (var option in request.Options.Properties())
				{
					if (ExecutionOptions.Contains(option.Name))
						continue;
					payload[option.Name] = option.Value.Clone(onError: exception => throw exception);
				}
			foreach (var name in new[] { "stream", "background" })
				if (payload[name] != null && payload[name].Type != JTokenType.Null && !JToken.DeepEquals(payload[name], new JValue(false)))
					throw new UnsupportedCapabilityException($"This provider adapter does not implement {name} execution");
			// Raw tool bags could bypass the execution-owned allow-list or require local mediation.
			if (payload["tools"] != null || payload["mcp_servers"] != null || payload["tool_choice"] != null)
				throw new UnsupportedCapabilityException("Tools must be resolved from the explicit MCP capability allow-list");
			return payload;
		}

		/// <summary>
		/// Renders normalized input and optional resource references as provider text
		/// </summary>
		/// <param name="request">The effective request with contextual references</param>
		/// <returns>The input string or compact JSON representation</returns>
		/// <remarks>References are included inline; this helper does not dereference resources or grant access.</remarks>
		protected static string GetInputText(RequestMessage request)
		{
			if (request.References == null || request.References.Count == 0)
				return request.Input?.Type == JTokenType.String ? request.Input.Value<string>() : request.Input?.AsString() ?? "null";
			// References provide inline identities/context only. Do not dereference or authorize resources here.
			return new JObject
			{
				["input"] = request.Input?.Clone(onError: exception => throw exception),
				["references"] = JArray.FromObject(request.References)
			}.AsString();
		}

		/// <summary>
		/// Selects exact MCP wire names from the explicit capability scope and resolved metadata
		/// </summary>
		/// <param name="request">The effective request with execution-owned MCP metadata</param>
		/// <returns>The distinct allowed tool names, or an empty list when scope is empty</returns>
		/// <exception cref="ProviderException">Required capability names or resolved metadata are invalid</exception>
		/// <exception cref="UnsupportedCapabilityException">Resolved metadata omits a requested tool</exception>
		protected static List<string> GetMcpToolNames(RequestMessage request)
		{
			if (request.Capabilities == null || request.Capabilities.Count == 0)
				return new List<string>();
			if (request.Capabilities.Any(string.IsNullOrWhiteSpace) || !(GetOption(request, "mcp-tools") is JArray tools))
				throw new ProviderException("MCP metadata must be resolved by the execution layer") { Code = "invalid-request" };
			var names = request.Capabilities.Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToList();
			var resolved = new HashSet<string>(tools.OfType<JObject>().Select(tool => tool["name"]?.Value<string>()), StringComparer.Ordinal);
			if (names.Any(name => !resolved.Contains(name)))
				throw new UnsupportedCapabilityException("MCP metadata does not contain every requested capability");
			return names;
		}

		/// <summary>
		/// Reads and validates the explicit remote MCP HTTP endpoint
		/// </summary>
		/// <param name="request">The effective request containing MCP connection options</param>
		/// <returns>The absolute endpoint URI</returns>
		/// <exception cref="ProviderException">The endpoint option is missing or invalid</exception>
		protected static string GetMcpEndpoint(RequestMessage request)
		{
			var value = GetOption(request, "mcp-endpoint");
			if (value?.Type != JTokenType.String || !Uri.TryCreate(value.Value<string>(), UriKind.Absolute, out var endpoint)
				|| (!endpoint.Scheme.IsEquals(Uri.UriSchemeHttps) && !endpoint.Scheme.IsEquals(Uri.UriSchemeHttp))
				|| !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
				throw new ProviderException("Remote MCP requires an explicit HTTP mcp-endpoint option") { Code = "invalid-configuration" };
			return endpoint.AbsoluteUri;
		}

		/// <summary>
		/// Parses one JSON value without converting date strings to date tokens
		/// </summary>
		/// <param name="text">The JSON text to read</param>
		/// <returns>The parsed token</returns>
		/// <exception cref="JsonException">Parsing fails or additional content follows the value</exception>
		protected internal static JToken ParseJson(string text)
		{
			using (var reader = new JsonTextReader(new StringReader(text ?? "")) { DateParseHandling = DateParseHandling.None })
			{
				var value = JToken.ReadFrom(reader);
				if (reader.Read())
					throw new JsonReaderException("Unexpected content after JSON value");
				return value;
			}
		}

		/// <summary>
		/// Converts provider text to free text or parsed structured output
		/// </summary>
		/// <param name="text">The provider output text</param>
		/// <param name="request">The effective request defining whether structured output is required</param>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <returns>The parsed token for structured output, or a text token for unstructured output</returns>
		/// <remarks>The execution layer still validates against the canonical schema. JSON parsing failure retains usage on the exception.</remarks>
		/// <exception cref="StructuredOutputException">Structured output cannot be parsed</exception>
		protected static JToken GetOutput(string text, RequestMessage request, UsageInfo usage)
		{
			if (request.Schema == null)
				return text == null ? null : new JValue(text);
			try
			{
				// Parse only; the execution layer must still validate against the canonical schema.
				return ParseJson(text);
			}
			catch (JsonException)
			{
				usage.Status = "error";
				var exception = new StructuredOutputException("AI provider returned invalid JSON for structured output");
				exception.Data["Usage"] = usage;
				throw exception;
			}
		}

		/// <summary>
		/// Reads an optional nonnegative 64-bit provider usage counter
		/// </summary>
		/// <param name="usage">The provider JSON usage object</param>
		/// <param name="name">The native counter property</param>
		/// <param name="attempt">The per-attempt usage attached to a normalized failure</param>
		/// <returns>The counter, or null when missing or JSON null</returns>
		/// <exception cref="ProviderException">The counter is negative, malformed or outside the supported range</exception>
		protected static long? GetTokenCount(JToken usage, string name, UsageInfo attempt)
		{
			var token = (usage as JObject)?[name];
			if (token == null || token.Type == JTokenType.Null)
				return null;
			if (token.Type != JTokenType.Integer || !Int64.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
				throw Failure(new ProviderException("AI provider returned an invalid usage counter") { Code = "invalid-response" }, attempt);
			return count;
		}

		/// <summary>
		/// Sums token categories using checked arithmetic
		/// </summary>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <param name="counts">The categories to sum; missing categories contribute zero</param>
		/// <returns>The total of the supplied categories</returns>
		/// <exception cref="ProviderException">The sum overflows; the attempt usage is attached to the failure</exception>
		protected static long SumTokenCounts(UsageInfo usage, params long?[] counts)
		{
			try
			{
				long total = 0;
				foreach (var count in counts)
					total = checked(total + (count ?? 0));
				return total;
			}
			catch (OverflowException)
			{
				throw Failure(new ProviderException("AI provider usage counters overflowed") { Code = "invalid-response" }, usage);
			}
		}

		/// <summary>
		/// Creates the existing usage contract for an attempt before transport starts
		/// </summary>
		/// <param name="model">The effective requested model</param>
		/// <returns>A usage record with provider context and zero dispatched model requests</returns>
		protected UsageInfo CreateUsage(string model)
			=> new UsageInfo
			{
				Provider = this.Info.Name,
				Model = model,
				Type = "ai",
				Operation = "execute",
				ModelRequests = 0,
				Status = "unset"
			};

		/// <summary>
		/// Preserves caller cancellation and attaches available attempt usage
		/// </summary>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <exception cref="OperationCanceledException">The supplied token is canceled</exception>
		protected static void CheckCancellation(CancellationToken cancellationToken, UsageInfo usage)
		{
			if (!cancellationToken.IsCancellationRequested)
				return;
			usage.Status = "canceled";
			var exception = new OperationCanceledException(cancellationToken);
			exception.Data["Usage"] = usage;
			throw exception;
		}

		/// <summary>
		/// Sends one JSON HTTP request through the existing Utility transport
		/// </summary>
		/// <param name="request">The effective request supplying the request-timeout option</param>
		/// <param name="payload">The native JSON request body</param>
		/// <param name="resource">The API resource appended to the profile base endpoint</param>
		/// <param name="headers">The provider authentication and API headers</param>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <returns>The parsed provider response object</returns>
		/// <remarks>No logical retry or provider-owned HTTP client is created. Transport failures are normalized with transient classification, Retry-After and available attempt usage.</remarks>
		/// <exception cref="OperationCanceledException">The supplied token is canceled</exception>
		/// <exception cref="ProviderException">Provider transport, response parsing or HTTP option validation fails</exception>
		protected async Task<JObject> SendAsync(RequestMessage request, JObject payload, string resource, Dictionary<string, string> headers, UsageInfo usage, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var timeout = 30;
			var timeoutOption = GetOption(request, "request-timeout");
			if (timeoutOption != null && (!Int32.TryParse(timeoutOption.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout <= 0))
				throw new ProviderException("request-timeout must be a positive number of seconds") { Code = "invalid-request" };
			var endpoint = new UriBuilder(this.Info.Endpoint);
			endpoint.Path = endpoint.Path.TrimEnd('/') + "/" + resource;
			var body = payload.AsString();
			usage.StartTime = DateTime.UtcNow;
			var stopwatch = Stopwatch.StartNew();
			try
			{
				using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint.Uri))
				{
					httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
					foreach (var header in headers)
						httpRequest.Headers.Add(header.Key, header.Value);
					usage.InputBytes = Encoding.UTF8.GetByteCount(body);
					usage.ModelRequests = 1;
					using (var response = await httpRequest.SendHttpRequestAsync(timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false))
					{
						var content = await response.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
						CheckCancellation(cancellationToken, usage);
						usage.OutputBytes = Encoding.UTF8.GetByteCount(content);
						if (!(ParseJson(content) is JObject json))
							throw new JsonReaderException("AI provider response must be a JSON object");
						usage.Status = "ok";
						return json;
					}
				}
			}
			catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
			{
				usage.Status = "canceled";
				exception.Data["Usage"] = usage;
				throw;
			}
			catch (RemoteServerException exception)
			{
				CheckCancellation(cancellationToken, usage);
				var status = (int)exception.StatusCode;
				var details = new JObject { ["http_status"] = status };
				string code = status.ToString(CultureInfo.InvariantCulture);
				try
				{
					var error = (ParseJson(exception.Body) as JObject)?["error"] as JObject;
					var providerCode = error?["code"] ?? error?["type"];
					if (providerCode?.Type == JTokenType.String || providerCode?.Type == JTokenType.Integer)
						code = providerCode.ToString();
				}
				catch (JsonException) { }
				usage.OutputBytes = exception.Body == null ? (long?)null : Encoding.UTF8.GetByteCount(exception.Body);
				// Do not retain the raw error body through an inner RemoteServerException.
				throw Failure(new ProviderException($"AI provider returned HTTP {status}")
				{
					Code = code,
					IsTransient = status == 408 || status == 429 || status >= 500 && status <= 599,
					RetryAfter = GetRetryAfter(exception.Headers),
					Details = details
				}, usage);
			}
			catch (ConnectionTimeoutException)
			{
				CheckCancellation(cancellationToken, usage);
				throw Failure(new ProviderException("AI provider request timed out") { Code = "request-timeout", IsTransient = true }, usage);
			}
			catch (HttpRequestException exception)
			{
				CheckCancellation(cancellationToken, usage);
				throw NetworkFailure(exception, usage);
			}
			catch (IOException exception)
			{
				CheckCancellation(cancellationToken, usage);
				throw NetworkFailure(exception, usage);
			}
			catch (AuthenticationException exception)
			{
				CheckCancellation(cancellationToken, usage);
				throw NetworkFailure(exception, usage);
			}
			catch (JsonException)
			{
				CheckCancellation(cancellationToken, usage);
				throw Failure(new ProviderException("AI provider returned an invalid JSON response") { Code = "invalid-response" }, usage);
			}
			catch (FormatException)
			{
				CheckCancellation(cancellationToken, usage);
				throw Failure(new ProviderException("AI provider HTTP headers are invalid") { Code = "invalid-configuration" }, usage);
			}
			finally
			{
				usage.EndTime = DateTime.UtcNow;
				usage.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
			}
		}

		/// <summary>
		/// Marks attempt usage as failed and attaches it to a normalized provider exception
		/// </summary>
		/// <param name="exception">The normalized failure to enrich</param>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <returns>The same exception with its attempt usage attached</returns>
		protected static ProviderException Failure(ProviderException exception, UsageInfo usage)
		{
			usage.Status = "error";
			exception.Data["Usage"] = usage;
			return exception;
		}

		/// <summary>
		/// Classifies network failures while keeping TLS authentication failures non-transient
		/// </summary>
		/// <param name="exception">The transport exception chain to inspect</param>
		/// <param name="usage">The existing per-attempt usage record</param>
		/// <returns>The normalized network or TLS failure carrying usage</returns>
		static ProviderException NetworkFailure(Exception exception, UsageInfo usage)
		{
			var cause = exception;
			while (cause != null && !(cause is AuthenticationException))
				cause = cause.InnerException;
			return Failure(new ProviderException(cause == null ? "AI provider network request failed" : "AI provider TLS authentication failed")
			{
				Code = cause == null ? "network-error" : "tls-error",
				IsTransient = cause == null
			}, usage);
		}

		/// <summary>
		/// Reads Retry-After as delta seconds or an HTTP date
		/// </summary>
		/// <param name="headers">The optional response headers to inspect without regard to key case</param>
		/// <returns>The retry interval, clamped to zero for a past date, or null when no supported value exists</returns>
		static TimeSpan? GetRetryAfter(Dictionary<string, string> headers)
		{
			var value = headers?.FirstOrDefault(header => "Retry-After".IsEquals(header.Key)).Value;
			if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
				return TimeSpan.FromSeconds(seconds);
			if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
			{
				var now = DateTimeOffset.UtcNow;
				return date > now ? date - now : TimeSpan.Zero;
			}
			return null;
		}
		#endregion
	}

	/// <summary>
	/// Owns case-insensitive provider profiles, atomic replacement and instance retirement
	/// </summary>
	/// <remarks>This is a separate internal class sharing the provider source file. Leases preserve the selected instance across an execution.</remarks>
	internal sealed class ProviderRegistry : IDisposable, IAsyncDisposable
	{
		#region Configuration loading and synchronous cleanup
		/// <summary>
		/// Loads initial profiles through the existing XML section handler and assembly helpers
		/// </summary>
		/// <returns>The initialized registry, empty when no AI section is configured</returns>
		/// <remarks>The section-level default is validated after profiles load. A failed load cleans up staged registry ownership before propagating the failure.</remarks>
		internal static async Task<ProviderRegistry> LoadAsync()
		{
			var registry = new ProviderRegistry();
			try
			{
				if (!(System.Configuration.ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:AI", "net.vieapps.services.ai")) is AppConfigurationSectionHandler config))
					return registry;

				var defaultProvider = config.Section.Attributes?["default"]?.Value?.Trim();
				var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (XmlNode node in config.Section.SelectNodes("provider"))
				{
					var info = new ProviderInfo
					{
						Name = node.Attributes?["name"]?.Value?.Trim(),
						Title = node.Attributes?["title"]?.Value,
						Description = node.Attributes?["description"]?.Value,
						Type = node.Attributes?["type"]?.Value?.Trim(),
						Endpoint = node.Attributes?["endpoint"]?.Value,
						Model = node.Attributes?["model"]?.Value,
						Credential = node.Attributes?["credential"]?.Value,
						Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					};
					if (string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Type))
						throw new ProviderException("AI provider configuration requires a name and CLR type") { Code = "invalid-configuration" };
					if (!names.Add(info.Name))
						throw new ProviderException("AI provider profile names must be unique, ignoring case") { Code = "invalid-configuration" };
					foreach (XmlNode option in node.SelectNodes("options/add"))
					{
						var key = option.Attributes?["key"]?.Value;
						if (string.IsNullOrWhiteSpace(key) || info.Options.ContainsKey(key))
							throw new ProviderException("AI provider options require unique, nonempty keys") { Code = "invalid-configuration" };
						info.Options.Add(key, option.Attributes?["value"]?.Value);
					}

					await registry.UpdateAsync(info, CancellationToken.None).ConfigureAwait(false);
				}
				registry.SetDefault(defaultProvider, CancellationToken.None);
				return registry;
			}
			catch
			{
				try
				{
					await registry.DisposeAsync().ConfigureAwait(false);
				}
				catch (Exception cleanupException)
				{
					WriteCleanupError(cleanupException);
				}
				throw;
			}
		}

		/// <summary>
		/// Runs registry cleanup synchronously through the existing Utility helper
		/// </summary>
		/// <remarks>Completion waits for outstanding leases, provider cleanup and pending initializations to drain.</remarks>
		public void Dispose()
			=> this.DisposeAsync().Execute(true);
		#endregion

		#region Registry state, updates and execution leases
		/// <summary>
		/// Protects profile publication, ownership, reference counts and retirement state
		/// </summary>
		readonly object SyncRoot = new object();
		/// <summary>
		/// Stores active profiles by case-insensitive logical name
		/// </summary>
		readonly Dictionary<string, Entry> Providers = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		/// <summary>
		/// Tracks active and retired entries until their cleanup finishes
		/// </summary>
		readonly HashSet<Entry> OwnedEntries = new HashSet<Entry>();
		/// <summary>
		/// Stores the configured default key without selecting an implicit fallback
		/// </summary>
		string DefaultProvider;
		/// <summary>
		/// Records whether registry shutdown has begun; accessed under SyncRoot
		/// </summary>
		bool Disposed;
		/// <summary>
		/// Counts staged profile updates not yet drained; accessed under SyncRoot
		/// </summary>
		int PendingUpdates;
		/// <summary>
		/// Signals completion of the current group of pending updates
		/// </summary>
		TaskCompletionSource<bool> UpdatesDrained;
		/// <summary>
		/// Stores the shared completion task returned by repeated registry cleanup calls
		/// </summary>
		Task ShutdownTask;

		/// <summary>
		/// Pins the selected provider for a logical execution
		/// </summary>
		/// <param name="name">The case-insensitive profile name; null or blank selects the configured default</param>
		/// <returns>A lease retaining this exact provider instance until release</returns>
		/// <exception cref="ProviderException">The requested profile or configured default cannot be resolved</exception>
		/// <exception cref="ObjectDisposedException">Registry shutdown has begun</exception>
		internal Lease Acquire(string name)
		{
			lock (this.SyncRoot)
			{
				this.ThrowIfDisposed();
				name = string.IsNullOrWhiteSpace(name) ? this.DefaultProvider : name.Trim();
				if (string.IsNullOrWhiteSpace(name) || !this.Providers.TryGetValue(name, out var entry))
					throw new ProviderException("The requested AI provider profile is not configured") { Code = "provider-not-found" };
				var lease = new Lease(this, entry);
				entry.Users = checked(entry.Users + 1);
				return lease;
			}
		}

		/// <summary>
		/// Initializes a new provider snapshot and atomically publishes its profile
		/// </summary>
		/// <param name="info">The configuration to copy before initialization</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <returns>A task completing after publication and any immediately eligible old-provider cleanup</returns>
		/// <remarks>Provider code runs outside SyncRoot. Existing leases keep the retired instance. Cancellation before publication leaves the active profile unchanged.</remarks>
		internal async Task UpdateAsync(ProviderInfo info, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (this.SyncRoot)
			{
				this.ThrowIfDisposed();
				if (this.PendingUpdates++ == 0)
					this.UpdatesDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			try
			{
				var entry = await CreateEntryAsync(info, cancellationToken).ConfigureAwait(false);
				Entry retired = null;
				var published = false;
				try
				{
					lock (this.SyncRoot)
					{
						this.ThrowIfDisposed();
						cancellationToken.ThrowIfCancellationRequested();
						this.Providers.TryGetValue(entry.Name, out var previous);
						this.OwnedEntries.Add(entry);
						this.Providers[entry.Name] = entry;
						published = true;
						if (previous != null && Retire(previous))
							retired = previous;
					}
				}
				finally
				{
					if (!published)
						await DisposeProviderAsync(entry.Provider).ConfigureAwait(false);
				}
				if (retired != null)
					await this.DisposeEntryAsync(retired).ConfigureAwait(false);
			}
			finally
			{
				TaskCompletionSource<bool> drained = null;
				lock (this.SyncRoot)
					if (--this.PendingUpdates == 0)
						drained = this.UpdatesDrained;
				drained?.TrySetResult(true);
			}
		}

		/// <summary>
		/// Removes a profile from future acquisition and retires its instance
		/// </summary>
		/// <param name="name">The case-insensitive profile key</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <returns>true when the profile was removed; otherwise false</returns>
		/// <remarks>The configured default key is retained. Existing executions keep their leases; immediately eligible cleanup is awaited.</remarks>
		internal async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("A provider profile name is required", nameof(name));
			Entry retired = null;
			lock (this.SyncRoot)
			{
				this.ThrowIfDisposed();
				cancellationToken.ThrowIfCancellationRequested();
				if (!this.Providers.TryGetValue(name.Trim(), out var entry))
					return false;
				this.Providers.Remove(name.Trim());
				// Keep the configured default key. Removing it must not select another profile.
				if (Retire(entry))
					retired = entry;
			}
			if (retired != null)
				await this.DisposeEntryAsync(retired).ConfigureAwait(false);
			return true;
		}

		/// <summary>
		/// Changes or clears the configured default under the registry lock
		/// </summary>
		/// <param name="name">An existing profile key, or null/blank to clear the default</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <exception cref="ProviderException">The requested nonempty profile key is absent</exception>
		internal void SetDefault(string name, CancellationToken cancellationToken)
		{
			name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
			lock (this.SyncRoot)
			{
				this.ThrowIfDisposed();
				cancellationToken.ThrowIfCancellationRequested();
				if (name != null && !this.Providers.ContainsKey(name))
					throw new ProviderException("The default AI provider profile is not configured") { Code = "provider-not-found" };
				this.DefaultProvider = name;
			}
		}

		/// <summary>
		/// Rejects operations after registry shutdown begins
		/// </summary>
		/// <remarks>The caller holds SyncRoot.</remarks>
		/// <exception cref="ObjectDisposedException">The registry is shutting down</exception>
		void ThrowIfDisposed()
		{
			if (this.Disposed)
				throw new ObjectDisposedException(nameof(ProviderRegistry));
		}

		// All reference counts and retirement flags are accessed under SyncRoot.
		/// <summary>
		/// Marks an entry retired and claims cleanup when its final user has released it
		/// </summary>
		/// <param name="entry">The entry whose retirement state is protected by SyncRoot</param>
		/// <returns>true only when this call claims the one-time cleanup start</returns>
		/// <remarks>The caller holds SyncRoot; provider cleanup is performed outside the lock.</remarks>
		static bool Retire(Entry entry)
		{
			entry.Retired = true;
			if (entry.Users != 0 || entry.DisposalStarted)
				return false;
			entry.DisposalStarted = true;
			return true;
		}

		/// <summary>
		/// Releases one provider lease and queues eligible retirement cleanup without awaiting it
		/// </summary>
		/// <param name="entry">The exact entry pinned by the released lease</param>
		/// <returns>An already completed operation after reference-count update and any cleanup scheduling</returns>
		/// <remarks>Provider disposal does not run on the releasing execution path. Entry.Disposal remains tracked for registry shutdown.</remarks>
		ValueTask ReleaseAsync(Entry entry)
		{
			var dispose = false;
			lock (this.SyncRoot)
			{
				entry.Users--;
				if (entry.Retired)
					dispose = Retire(entry);
			}
			// Entry.Disposal remains tracked by the registry until cleanup completes.
			// Queue cleanup so even synchronous provider disposal cannot hold up the execution releasing its lease.
			if (dispose)
				_ = Task.Run(() => this.DisposeEntryAsync(entry));
			return default;
		}

		/// <summary>
		/// Attempts provider cleanup and completes registry retirement tracking
		/// </summary>
		/// <param name="entry">The retired entry whose cleanup start has already been claimed</param>
		/// <returns>A task completing after cleanup has been attempted and ownership tracking is released</returns>
		/// <remarks>Provider cleanup failures are logged; completion does not assert that a failing cleanup succeeded.</remarks>
		async Task DisposeEntryAsync(Entry entry)
		{
			try { await DisposeProviderAsync(entry.Provider).ConfigureAwait(false); }
			finally
			{
				lock (this.SyncRoot)
					this.OwnedEntries.Remove(entry);
				entry.Disposal.TrySetResult(true);
			}
		}

		/// <summary>
		/// Prevents new acquisition/publication and drains all registry-owned provider lifetimes
		/// </summary>
		/// <returns>The shared task waiting for leases, tracked cleanups and pending updates</returns>
		/// <remarks>Cleanup is not canceled by a completed execution budget. Repeated calls return the same shutdown task.</remarks>
		public ValueTask DisposeAsync()
		{
			List<Entry> dispose;
			Task completion;
			lock (this.SyncRoot)
			{
				if (this.ShutdownTask != null)
					return new ValueTask(this.ShutdownTask);
				this.Disposed = true;
				this.Providers.Clear();
				this.DefaultProvider = null;
				dispose = this.OwnedEntries.Where(Retire).ToList();
				var tasks = this.OwnedEntries.Select(entry => entry.Disposal.Task).Cast<Task>().ToList();
				if (this.PendingUpdates > 0)
					tasks.Add(this.UpdatesDrained.Task);
				completion = this.ShutdownTask = Task.WhenAll(tasks);
			}
			foreach (var entry in dispose)
				_ = this.DisposeEntryAsync(entry);
			return new ValueTask(completion);
		}

		/// <summary>
		/// Copies, resolves, initializes and validates a provider before publication
		/// </summary>
		/// <param name="info">The proposed profile configuration</param>
		/// <param name="cancellationToken">The token used to cancel this operation</param>
		/// <returns>An unpublished registry entry with its own configuration snapshot</returns>
		/// <remarks>Initialization failure or pre-publication cancellation attempts cleanup of the newly created instance.</remarks>
		static async Task<Entry> CreateEntryAsync(ProviderInfo info, CancellationToken cancellationToken)
		{
			if (info == null)
				throw new ArgumentNullException(nameof(info));
			var snapshot = info.Copy(onError: exception => throw exception);
			snapshot.Name = snapshot.Name?.Trim();
			snapshot.Type = snapshot.Type?.Trim();
			if (string.IsNullOrWhiteSpace(snapshot.Name) || string.IsNullOrWhiteSpace(snapshot.Type))
				throw new ProviderException("AI provider configuration requires a name and CLR type") { Code = "invalid-configuration" };
			snapshot.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (info.Options != null)
				foreach (var option in info.Options)
				{
					if (string.IsNullOrWhiteSpace(option.Key) || snapshot.Options.ContainsKey(option.Key))
						throw new ProviderException("AI provider options require unique, nonempty keys") { Code = "invalid-configuration" };
					snapshot.Options.Add(option.Key, option.Value);
				}
			cancellationToken.ThrowIfCancellationRequested();
			var name = snapshot.Name;
			var typeName = snapshot.Type;
			var type = AssemblyLoader.GetType(typeName);
			if (type == null || type.IsAbstract || type.ContainsGenericParameters || !typeof(Provider).IsAssignableFrom(type))
				throw new ProviderException("The configured AI provider type must implement Provider") { Code = "invalid-configuration" };
			var provider = type.CreateInstance() as Provider;
			if (provider == null)
				throw new ProviderException("Cannot create the configured AI provider") { Code = "invalid-configuration" };
			try
			{
				provider.Initialize(snapshot);
				cancellationToken.ThrowIfCancellationRequested();
				if (provider.Info == null || !name.IsEquals(provider.Info.Name) || !typeName.IsEquals(provider.Info.Type, StringComparison.Ordinal))
					throw new ProviderException("AI provider initialization did not preserve its profile identity and CLR type") { Code = "invalid-configuration" };
				return new Entry(name, provider);
			}
			catch
			{
				await DisposeProviderAsync(provider).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				throw;
			}
		}

		/// <summary>
		/// Attempts asynchronous provider cleanup while diagnosing and suppressing cleanup failures
		/// </summary>
		/// <param name="provider">The provider instance to clean up</param>
		/// <returns>A task completing after cleanup has been attempted</returns>
		static async Task DisposeProviderAsync(Provider provider)
		{
			try { await provider.DisposeAsync().ConfigureAwait(false); }
			catch (Exception exception) { WriteCleanupError(exception); }
		}

		/// <summary>
		/// Writes provider cleanup failures through existing ServiceLog infrastructure
		/// </summary>
		/// <param name="exception">The cleanup failure to diagnose</param>
		/// <remarks>Diagnostic logging failures are suppressed to preserve the original operation outcome.</remarks>
		static void WriteCleanupError(Exception exception)
		{
			try
			{
				global::net.vieapps.Services.Extensions.WriteServiceLog(null, null, null, ServiceBase.ServiceComponent?.Logger,
					new List<string> { "Error occurred while disposing an AI provider" }, exception, ServiceBase.ServiceComponent?.ServiceName, "AI");
			}
			catch { }
		}

		/// <summary>
		/// Tracks one provider instance, its users and retirement completion
		/// </summary>
		internal sealed class Entry
		{
			/// <summary>
			/// Creates ownership tracking for an initialized provider instance
			/// </summary>
			/// <param name="name">The normalized logical profile key</param>
			/// <param name="provider">The initialized provider owned by this entry</param>
			internal Entry(string name, Provider provider)
			{
				this.Name = name;
				this.Provider = provider;
			}
			/// <summary>
			/// Gets the normalized logical profile key
			/// </summary>
			internal string Name { get; }
			/// <summary>
			/// Gets the exact provider instance owned by this entry
			/// </summary>
			internal Provider Provider { get; }
			/// <summary>
			/// Counts active leases; accessed under the owning registry lock
			/// </summary>
			internal int Users;
			/// <summary>
			/// Records whether the entry is unavailable to future acquisition; accessed under the registry lock
			/// </summary>
			internal bool Retired;
			/// <summary>
			/// Records the one-time cleanup claim; accessed under the registry lock
			/// </summary>
			internal bool DisposalStarted;
			/// <summary>
			/// Signals that provider cleanup has been attempted and retirement tracking released
			/// </summary>
			internal TaskCompletionSource<bool> Disposal { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		/// <summary>
		/// Pins one registry entry and releases its reference at most once
		/// </summary>
		internal sealed class Lease : IDisposable, IAsyncDisposable
		{
			/// <summary>
			/// Stores the registry until the first atomic lease release
			/// </summary>
			ProviderRegistry Owner;
			/// <summary>
			/// Retains the exact provider entry selected for this lease
			/// </summary>
			readonly Entry Entry;
			/// <summary>
			/// Creates a lease for a provider entry whose reference count is managed by its registry
			/// </summary>
			/// <param name="owner">The registry owning the entry</param>
			/// <param name="entry">The exact provider entry selected for execution</param>
			internal Lease(ProviderRegistry owner, Entry entry)
			{
				this.Owner = owner;
				this.Entry = entry;
			}
			/// <summary>
			/// Gets the provider instance pinned for this execution
			/// </summary>
			internal Provider Provider => this.Entry.Provider;
			/// <summary>
			/// Releases the pinned reference once without waiting for retired-provider cleanup
			/// </summary>
			/// <returns>The lease-release operation, or an already completed operation after a prior release</returns>
			public ValueTask DisposeAsync()
			{
				var owner = Interlocked.Exchange(ref this.Owner, null);
				return owner == null ? default : owner.ReleaseAsync(this.Entry);
			}
			/// <summary>
			/// Releases the lease synchronously through the existing Utility helper
			/// </summary>
			public void Dispose()
				=> this.DisposeAsync().Execute(true);
		}
		#endregion
	}

	#region Provider profile configuration
	/// <summary>
	/// Presents the configuration boundary of a logical provider profile
	/// </summary>
	/// <remarks>The registry copies this configuration before initializing a replacement instance. Active profiles are not updated by mutating the supplied object.</remarks>
	public class ProviderInfo
	{
		/// <summary>
		/// Gets or Sets the unique logical provider-profile key, resolved without regard to case
		/// </summary>
		public string Name { get; set; }
		/// <summary>
		/// Gets or Sets the optional friendly display name
		/// </summary>
		public string Title { get; set; }
		/// <summary>
		/// Gets or Sets the optional description of the profile purpose
		/// </summary>
		public string Description { get; set; }
		/// <summary>
		/// Gets or Sets the CLR implementation type resolved by the existing assembly loader
		/// </summary>
		public string Type { get; set; }
		/// <summary>
		/// Gets or Sets the base endpoint of the provider API or gateway
		/// </summary>
		public string Endpoint { get; set; }
		/// <summary>
		/// Gets or Sets the default model, subject to effective request overrides
		/// </summary>
		public string Model { get; set; }
		/// <summary>
		/// Gets or Sets the credential material used to authenticate the provider API request
		/// </summary>
		public string Credential { get; set; }
		/// <summary>
		/// Gets or Sets provider-specific defaults applied before request options and business request overrides
		/// </summary>
		public Dictionary<string, string> Options { get; set; }
	}
	#endregion

	#region OpenAI reference provider
	/// <summary>
	/// Adapts a single non-streaming OpenAI Responses API request
	/// </summary>
	/// <remarks>Uses the existing Utility HTTP transport and common message contracts. Provider selection, logical retry and canonical schema validation remain in the execution layer.</remarks>
	public class OpenAIProvider : Provider
	{
		/// <summary>
		/// Validates and assigns configuration before this provider is published
		/// </summary>
		/// <param name="info">The registry-owned profile snapshot</param>
		/// <exception cref="ProviderException">Required profile values or the base endpoint are invalid</exception>
		internal override void Initialize(ProviderInfo info)
		{
			ValidateConfiguration(info);
			base.Initialize(info);
		}

		/// <summary>
		/// Determines whether this adapter implements the requested logical capability
		/// </summary>
		/// <param name="capability">The logical capability name, matched without regard to case</param>
		/// <returns>true for structured-output, remote-mcp, tools or reasoning; otherwise false</returns>
		/// <remarks>Tools are limited to resolved remote MCP. Actual model support is enforced by the provider API.</remarks>
		public override bool Supports(string capability)
			=> "structured-output".IsEquals(capability)
				|| "remote-mcp".IsEquals(capability)
				|| "tools".IsEquals(capability)
				|| "reasoning".IsEquals(capability);

		/// <summary>
		/// Builds, sends and normalizes one non-streaming OpenAI Responses API attempt
		/// </summary>
		/// <param name="request">The effective request with materialized schema, native options and resolved MCP metadata</param>
		/// <param name="cancellationToken">The token covering caller cancellation and execution deadlines</param>
		/// <returns>The normalized response with available per-attempt usage and selected metadata</returns>
		/// <remarks>The canonical schema is adapted without changing its constraints. Only execution-resolved MCP tool names are exposed. No logical retry or local tool mediation is performed.</remarks>
		/// <exception cref="OperationCanceledException">The supplied token is canceled</exception>
		/// <exception cref="ProviderException">Configuration, native options, transport or provider response validation fails</exception>
		/// <exception cref="UnsupportedCapabilityException">Native options request an unimplemented execution mode or bypass the MCP allow-list</exception>
		/// <exception cref="StructuredOutputException">Completed structured output cannot be parsed</exception>
		public override async Task<ResponseMessage> ExecuteAsync(RequestMessage request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var model = this.GetModel(request);
			var payload = GetPayloadOptions(request);
			payload["model"] = model;
			payload["instructions"] = request.Instructions;
			payload["input"] = GetInputText(request);
			if (payload["messages"] != null || payload["system"] != null)
				throw new ProviderException("OpenAI Responses uses RequestMessage instructions and input") { Code = "invalid-request" };
			if (payload["text"] != null && !(payload["text"] is JObject))
				throw new ProviderException("OpenAI text options must be a JSON object") { Code = "invalid-request" };
			var textOptions = payload["text"] as JObject ?? new JObject();
			if (textOptions["format"] != null)
				throw new ProviderException("Use RequestMessage.Schema for structured output") { Code = "invalid-request" };
			if (request.Schema != null)
			{
				textOptions["format"] = new JObject
				{
					["type"] = "json_schema",
					["name"] = "output",
					["strict"] = true,
					["schema"] = request.Schema.Clone(onError: exception => throw exception)
				};
				payload["text"] = textOptions;
			}

			var tools = GetMcpToolNames(request);
			if (tools.Count > 0)
			{
				var mcp = new JObject
				{
					["type"] = "mcp",
					["server_label"] = GetOption(request, "mcp-server-label")?.Clone(onError: exception => throw exception) ?? new JValue("vieapps"),
					["server_url"] = GetMcpEndpoint(request),
					["allowed_tools"] = new JArray(tools),
					// The execution layer already resolved the explicit allow-list. No approval callback loop is used.
					["require_approval"] = "never"
				};
				var authorization = GetOption(request, "mcp-authorization");
				if (authorization != null && authorization.Type != JTokenType.Null)
					mcp["authorization"] = authorization.Clone(onError: exception => throw exception);
				payload["tools"] = new JArray(mcp);
			}

			var usage = this.CreateUsage(model);
			var json = await this.SendAsync(request, payload, "responses", new Dictionary<string, string>
			{
				["Authorization"] = $"Bearer {this.Info.Credential}"
			}, usage, cancellationToken).ConfigureAwait(false);
			CheckCancellation(cancellationToken, usage);
			var response = NormalizeResponse(request, json, usage);
			CheckCancellation(cancellationToken, usage);
			return response;
		}

		/// <summary>
		/// Maps the OpenAI response into common output, status, usage and diagnostic contracts
		/// </summary>
		/// <param name="request">The effective request defining structured-output parsing</param>
		/// <param name="json">The parsed provider response</param>
		/// <param name="usage">The existing attempt record to enrich with native usage and tool counters</param>
		/// <returns>The normalized response without retaining the full raw provider response</returns>
		/// <remarks>Completed structured text is parsed here and canonically validated by the execution layer. Refusal has no output; incomplete text retains its non-completed status.</remarks>
		/// <exception cref="ProviderException">The response or native usage counters are invalid, or the provider reports a normalized failure</exception>
		/// <exception cref="StructuredOutputException">Completed structured output cannot be parsed</exception>
		internal static ResponseMessage NormalizeResponse(RequestMessage request, JObject json, UsageInfo usage)
		{
			var providerUsage = json["usage"] as JObject;
			usage.InputTokens = GetTokenCount(providerUsage, "input_tokens", usage);
			usage.CachedInputTokens = GetTokenCount(providerUsage?["input_tokens_details"], "cached_tokens", usage);
			usage.OutputTokens = GetTokenCount(providerUsage, "output_tokens", usage);
			usage.ReasoningTokens = GetTokenCount(providerUsage?["output_tokens_details"], "reasoning_tokens", usage);
			usage.TotalTokens = GetTokenCount(providerUsage, "total_tokens", usage);
			if (json["model"]?.Type == JTokenType.String)
				usage.Model = json["model"].Value<string>();
			var status = json["status"]?.Type == JTokenType.String ? json["status"].Value<string>() : null;
			if (string.IsNullOrWhiteSpace(status))
				throw Failure(new ProviderException("OpenAI response is missing status") { Code = "invalid-response" }, usage);
			if ("failed".IsEquals(status, StringComparison.Ordinal) || "cancelled".IsEquals(status, StringComparison.Ordinal))
			{
				var code = (json["error"] as JObject)?["code"]?.ToString() ?? status;
				throw Failure(new ProviderException("OpenAI response did not complete")
				{
					Code = code,
					IsTransient = "server_error".IsEquals(code, StringComparison.Ordinal) || "rate_limit_exceeded".IsEquals(code, StringComparison.Ordinal),
					Details = new JObject { ["status"] = status }
				}, usage);
			}
			if (!(json["output"] is JArray output))
				throw Failure(new ProviderException("OpenAI response is missing output") { Code = "invalid-response" }, usage);
			var blocks = output.OfType<JObject>().Where(item => "message".IsEquals(item["type"]?.ToString(), StringComparison.Ordinal))
				.SelectMany(item => (item["content"] as JArray ?? new JArray()).OfType<JObject>()).ToList();
			var refused = blocks.Any(block => "refusal".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal));
			var requiresAction = output.OfType<JObject>().Any(item => "function_call".IsEquals(item["type"]?.ToString(), StringComparison.Ordinal) || "mcp_approval_request".IsEquals(item["type"]?.ToString(), StringComparison.Ordinal));
			var texts = blocks.Where(block => "output_text".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal) && block["text"]?.Type == JTokenType.String).Select(block => block["text"].Value<string>()).ToList();
			usage.MCPCalls = output.OfType<JObject>().Count(item => "mcp_call".IsEquals(item["type"]?.ToString(), StringComparison.Ordinal));
			usage.ToolCalls = usage.MCPCalls + output.OfType<JObject>().Count(item => "function_call".IsEquals(item["type"]?.ToString(), StringComparison.Ordinal));
			status = refused ? "refused" : requiresAction ? "requires-action" : status;
			if ("completed".IsEquals(status, StringComparison.Ordinal) && texts.Count == 0)
				throw Failure(new ProviderException("OpenAI completed without text output") { Code = "invalid-response" }, usage);
			usage.Status = "completed".IsEquals(status, StringComparison.Ordinal) ? "ok" : status;
			return new ResponseMessage
			{
				Output = refused || texts.Count == 0 ? null : "completed".IsEquals(status, StringComparison.Ordinal)
					? GetOutput(String.Concat(texts), request, usage)
					: new JValue(String.Concat(texts)),
				Status = status,
				Usage = usage,
				Details = new JObject
				{
					["id"] = json["id"]?.Clone(onError: exception => throw exception),
					["status"] = json["status"]?.Clone(onError: exception => throw exception),
					["incomplete_reason"] = (json["incomplete_details"] as JObject)?["reason"]?.Clone(onError: exception => throw exception)
				}
			};
		}
	}
	#endregion

	#region Anthropic reference provider
	/// <summary>
	/// Adapts a single non-streaming Anthropic Messages API request
	/// </summary>
	/// <remarks>Uses the existing Utility HTTP transport and common message contracts. Provider selection, logical retry and canonical schema validation remain in the execution layer.</remarks>
	public class AnthropicProvider : Provider
	{
		/// <summary>
		/// Validates and assigns configuration before this provider is published
		/// </summary>
		/// <param name="info">The registry-owned profile snapshot</param>
		/// <exception cref="ProviderException">Required profile values or the base endpoint are invalid</exception>
		internal override void Initialize(ProviderInfo info)
		{
			ValidateConfiguration(info);
			base.Initialize(info);
		}

		/// <summary>
		/// Determines whether this adapter implements the requested logical capability
		/// </summary>
		/// <param name="capability">The logical capability name, matched without regard to case</param>
		/// <returns>true for structured-output, remote-mcp, tools or reasoning; otherwise false</returns>
		/// <remarks>Tools are limited to resolved remote MCP. Actual model support is enforced by the provider API.</remarks>
		public override bool Supports(string capability)
			=> "structured-output".IsEquals(capability)
				|| "remote-mcp".IsEquals(capability)
				|| "tools".IsEquals(capability)
				|| "reasoning".IsEquals(capability);

		/// <summary>
		/// Builds, sends and normalizes one non-streaming Anthropic Messages API attempt
		/// </summary>
		/// <param name="request">The effective request with materialized schema, native options and resolved MCP metadata</param>
		/// <param name="cancellationToken">The token covering caller cancellation and execution deadlines</param>
		/// <returns>The normalized response with available per-attempt usage and selected metadata</returns>
		/// <remarks>The canonical schema is adapted without changing its constraints. Only execution-resolved MCP tool names are exposed. No logical retry or local tool mediation is performed. A positive max_tokens option is required.</remarks>
		/// <exception cref="OperationCanceledException">The supplied token is canceled</exception>
		/// <exception cref="ProviderException">Configuration, native options, transport or provider response validation fails</exception>
		/// <exception cref="UnsupportedCapabilityException">Native options request an unimplemented execution mode or bypass the MCP allow-list</exception>
		/// <exception cref="StructuredOutputException">Completed structured output cannot be parsed</exception>
		public override async Task<ResponseMessage> ExecuteAsync(RequestMessage request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var model = this.GetModel(request);
			var payload = GetPayloadOptions(request);
			payload["model"] = model;
			if (payload["input"] != null || payload["instructions"] != null)
				throw new ProviderException("Anthropic Messages uses RequestMessage instructions and input") { Code = "invalid-request" };
			if (string.IsNullOrWhiteSpace(request.Instructions))
				payload.Remove("system");
			else
				payload["system"] = request.Instructions;
			payload["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = GetInputText(request) });
			if (payload["max_tokens"]?.Type != JTokenType.Integer || !Int32.TryParse(payload["max_tokens"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens) || maxTokens <= 0)
				throw new ProviderException("Anthropic requires a positive max_tokens option") { Code = "invalid-request" };
			if (payload["output_config"] != null && !(payload["output_config"] is JObject))
				throw new ProviderException("Anthropic output_config must be a JSON object") { Code = "invalid-request" };
			var outputConfig = payload["output_config"] as JObject ?? new JObject();
			if (outputConfig["format"] != null || payload["output_format"] != null)
				throw new ProviderException("Use RequestMessage.Schema for structured output") { Code = "invalid-request" };
			if (request.Schema != null)
			{
				outputConfig["format"] = new JObject { ["type"] = "json_schema", ["schema"] = request.Schema.Clone(onError: exception => throw exception) };
				payload["output_config"] = outputConfig;
			}

			var headers = new Dictionary<string, string>
			{
				["x-api-key"] = this.Info.Credential,
				["anthropic-version"] = GetOption(request, "anthropic-version")?.ToString() ?? "2023-06-01"
			};
			var beta = GetOption(request, "anthropic-beta")?.ToString();
			var tools = GetMcpToolNames(request);
			if (tools.Count > 0)
			{
				var name = GetOption(request, "mcp-server-label")?.Clone(onError: exception => throw exception) ?? new JValue("vieapps");
				var server = new JObject { ["type"] = "url", ["name"] = name, ["url"] = GetMcpEndpoint(request) };
				var authorization = GetOption(request, "mcp-authorization");
				if (authorization != null && authorization.Type != JTokenType.Null)
					server["authorization_token"] = authorization.Clone(onError: exception => throw exception);
				var configs = new JObject();
				foreach (var tool in tools)
					configs[tool] = new JObject { ["enabled"] = true };
				payload["mcp_servers"] = new JArray(server);
				payload["tools"] = new JArray(new JObject
				{
					["type"] = "mcp_toolset",
					["mcp_server_name"] = name.Clone(onError: exception => throw exception),
					["default_config"] = new JObject { ["enabled"] = false },
					["configs"] = configs
				});
				var betas = (beta ?? "").Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
				if (!betas.Any(value => value.IsStartsWith("mcp-client-", StringComparison.Ordinal)))
					betas.Add("mcp-client-2025-11-20");
				beta = String.Join(",", betas);
			}
			if (!string.IsNullOrWhiteSpace(beta))
				headers["anthropic-beta"] = beta;

			var usage = this.CreateUsage(model);
			var json = await this.SendAsync(request, payload, "messages", headers, usage, cancellationToken).ConfigureAwait(false);
			CheckCancellation(cancellationToken, usage);
			var response = NormalizeResponse(request, json, usage);
			CheckCancellation(cancellationToken, usage);
			return response;
		}

		/// <summary>
		/// Maps the Anthropic response into common output, status, usage and diagnostic contracts
		/// </summary>
		/// <param name="request">The effective request defining structured-output parsing</param>
		/// <param name="json">The parsed provider response</param>
		/// <param name="usage">The existing attempt record to enrich with native usage and tool counters</param>
		/// <returns>The normalized response without retaining the full raw provider response</returns>
		/// <remarks>Completed structured text is parsed here and canonically validated by the execution layer. Refusal has no output; incomplete text retains its non-completed status.</remarks>
		/// <exception cref="ProviderException">The response or native usage counters are invalid, or the provider reports a normalized failure</exception>
		/// <exception cref="StructuredOutputException">Completed structured output cannot be parsed</exception>
		internal static ResponseMessage NormalizeResponse(RequestMessage request, JObject json, UsageInfo usage)
		{
			var providerUsage = json["usage"] as JObject;
			var uncachedInput = GetTokenCount(providerUsage, "input_tokens", usage);
			usage.CachedInputTokens = GetTokenCount(providerUsage, "cache_read_input_tokens", usage);
			usage.CacheCreationInputTokens = GetTokenCount(providerUsage, "cache_creation_input_tokens", usage);
			// Anthropic reports uncached input separately; canonical input includes all three categories.
			usage.InputTokens = uncachedInput.HasValue ? SumTokenCounts(usage, uncachedInput, usage.CachedInputTokens, usage.CacheCreationInputTokens) : (long?)null;
			usage.OutputTokens = GetTokenCount(providerUsage, "output_tokens", usage);
			usage.ReasoningTokens = GetTokenCount(providerUsage?["output_tokens_details"], "thinking_tokens", usage);
			usage.TotalTokens = usage.InputTokens.HasValue && usage.OutputTokens.HasValue ? SumTokenCounts(usage, usage.InputTokens, usage.OutputTokens) : (long?)null;
			if (json["model"]?.Type == JTokenType.String)
				usage.Model = json["model"].Value<string>();
			if (!"message".IsEquals(json["type"]?.ToString(), StringComparison.Ordinal) || !(json["content"] is JArray content) || json["stop_reason"]?.Type != JTokenType.String)
				throw Failure(new ProviderException("Anthropic returned an invalid message response") { Code = "invalid-response" }, usage);
			var reason = json["stop_reason"].Value<string>();
			var status = "end_turn".IsEquals(reason, StringComparison.Ordinal) || "stop_sequence".IsEquals(reason, StringComparison.Ordinal) ? "completed"
				: "refusal".IsEquals(reason, StringComparison.Ordinal) ? "refused"
				: "tool_use".IsEquals(reason, StringComparison.Ordinal) ? "requires-action"
				: "incomplete";
			var blocks = content.OfType<JObject>().ToList();
			var texts = blocks.Where(block => "text".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal) && block["text"]?.Type == JTokenType.String).Select(block => block["text"].Value<string>()).ToList();
			usage.MCPCalls = blocks.Count(block => "mcp_tool_use".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal));
			usage.ToolCalls = usage.MCPCalls + blocks.Count(block => "tool_use".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal) || "server_tool_use".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal));
			if (blocks.Any(block => "tool_use".IsEquals(block["type"]?.ToString(), StringComparison.Ordinal)))
				status = "requires-action";
			if ("completed".IsEquals(status, StringComparison.Ordinal) && texts.Count == 0)
				throw Failure(new ProviderException("Anthropic completed without text output") { Code = "invalid-response" }, usage);
			usage.Status = "completed".IsEquals(status, StringComparison.Ordinal) ? "ok" : status;
			return new ResponseMessage
			{
				Output = "refused".IsEquals(status, StringComparison.Ordinal) || texts.Count == 0 ? null : "completed".IsEquals(status, StringComparison.Ordinal)
					? GetOutput(String.Concat(texts), request, usage)
					: new JValue(String.Concat(texts)),
				Status = status,
				Usage = usage,
				Details = new JObject { ["id"] = json["id"]?.Clone(onError: exception => throw exception), ["stop_reason"] = reason }
			};
		}
	}
	#endregion
}
