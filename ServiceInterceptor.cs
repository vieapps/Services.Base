#region Related components
using System;
using System.Reflection;

using WampSharp.V2;
using WampSharp.V2.Core.Contracts;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a registration interceptor of a service
	/// </summary>
	public class RegistrationInterceptor : CalleeRegistrationInterceptor
	{
		string _name;

		public RegistrationInterceptor(string name, RegisterOptions options = null) : base(options != null ? options : new RegisterOptions() { Invoke = WampInvokePolicy.Roundrobin })
		{
			this._name = name;
		}

		public override string GetProcedureUri(MethodInfo method)
		{
			return string.Format(base.GetProcedureUri(method), this._name);
		}
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a proxy interceptor for calling a service
	/// </summary>
	public class ProxyInterceptor : CalleeProxyInterceptor
	{
		string _name;

		public ProxyInterceptor(string name, CallOptions options = null) : base(options != null ? options : new CallOptions())
		{
			this._name = name;
		}

		public override string GetProcedureUri(MethodInfo method)
		{
			return string.Format(base.GetProcedureUri(method), this._name);
		}
	}
}