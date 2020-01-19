/* part of Pyrolite, by Irmen de Jong (irmen@razorvine.net) */
	
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable InconsistentNaming
// ReSharper disable MemberCanBeMadeStatic.Global

namespace Razorvine.Pyro
{
	/// <summary>
	/// Abstract base class of all Pyro serializers.
	/// </summary>
	public abstract class PyroSerializer
	{
		public abstract ushort serializer_id { get; }  // make sure this matches the id from Pyro

		public abstract byte[] serializeCall(string objectId, string method, object[] vargs, IDictionary<string, object> kwargs);
		public abstract byte[] serializeData(object obj);
		public abstract object deserializeData(byte[] data);

		protected static SerpentSerializer serpentSerializer;
		
		public static PyroSerializer GetSerpentSerializer()
		{
			// Create a serpent serializer if not yet created.
			// This is done dynamically so there is no assembly dependency on the Serpent assembly,
			// and it will become available once you copy that into the correct location.
			lock(typeof(SerpentSerializer))
			{
				if (serpentSerializer != null) return serpentSerializer;
				try {
					serpentSerializer = new SerpentSerializer();
					return serpentSerializer;
				} catch (TypeInitializationException x) {
					throw new PyroException("serpent serializer unavailable", x);
				}
			}
		}

		public static PyroSerializer GetFor(int serializer_id)
		{
			if(serializer_id == serpentSerializer?.serializer_id)
				return serpentSerializer;
			
			throw new ArgumentException("unsupported serializer id: "+serializer_id);
		}
	}

	
	/// <summary>
	/// Serializer using the serpent protocol.
	/// Uses dynamic access to the Serpent assembly types (with reflection) to avoid
	/// a required assembly dependency with that.
	/// </summary>
	public class SerpentSerializer : PyroSerializer
	{
		private static readonly MethodInfo serializeMethod;
		private static readonly MethodInfo parseMethod;
		private static readonly MethodInfo astGetDataMethod;
		private static readonly MethodInfo tobytesMethod;
		private static readonly Type serpentSerializerType;
		private static readonly Type serpentParserType;

		public override ushort serializer_id => Message.SERIALIZER_SERPENT;

		static SerpentSerializer()
		{
			Assembly serpentAssembly = Assembly.Load("Razorvine.Serpent");
			Version serpentVersion = serpentAssembly.GetName().Version;
			Version requiredSerpentVersion = new Version(1, 29);
			if(serpentVersion<requiredSerpentVersion)
				throw new NotSupportedException("serpent version "+requiredSerpentVersion+" (or newer) is required");

			serpentSerializerType = serpentAssembly.GetType("Razorvine.Serpent.Serializer");
			serpentParserType = serpentAssembly.GetType("Razorvine.Serpent.Parser");
			Type astType = serpentAssembly.GetType("Razorvine.Serpent.Ast");
			
			serializeMethod = serpentSerializerType.GetMethod("Serialize", new [] {typeof(object)});
			parseMethod = serpentParserType.GetMethod("Parse", new [] {typeof(byte[])});
			tobytesMethod = serpentParserType.GetMethod("ToBytes", new [] {typeof(object)});

			astGetDataMethod = astType.GetMethod("GetData", new []{typeof(Func<IDictionary, object>)});
			
			// register a few custom class-to-dict converters
			MethodInfo registerMethod = serpentSerializerType.GetMethod("RegisterClass", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
			if (registerMethod == null)
				throw new PyroException("serpent library doesn't provide expected RegisterClass method");

			Func<object, IDictionary> converter = PyroUriPickler.ToSerpentDict;
			registerMethod.Invoke(null, new object[]{typeof(PyroURI), converter});
			converter = PyroExceptionPickler.ToSerpentDict;
			registerMethod.Invoke(null, new object[]{typeof(PyroException), converter});
			converter = PyroProxyPickler.ToSerpentDict;
			registerMethod.Invoke(null, new object[]{typeof(PyroProxy), converter});
		}
	
		public object DictToInstance(IDictionary dict)
		{
			string classname = (string)dict["__class__"];
			bool isException = dict.Contains("__exception__") && (bool)dict["__exception__"];
			if(isException)
			{
				// map all exception types to the PyroException
				return PyroExceptionPickler.FromSerpentDict(dict);
			}
			switch(classname)
			{
				case "Pyro4.core.URI":
					return PyroUriPickler.FromSerpentDict(dict);
				case "Pyro4.core.Proxy":
					return PyroProxyPickler.FromSerpentDict(dict);
				default:
					return null;
			}
		}

		public override byte[] serializeData(object obj)
		{
			// call the "Serialize" method, using reflection
			var serializer = Activator.CreateInstance(serpentSerializerType, Config.SERPENT_INDENT, Config.SERPENT_SET_LITERALS, true);
			return (byte[]) serializeMethod.Invoke(serializer, new [] {obj});
		}
		
		public override byte[] serializeCall(string objectId, string method, object[] vargs, IDictionary<string, object> kwargs)
		{
			object[] invokeparams = {objectId, method, vargs, kwargs};
			// call the "Serialize" method, using reflection
			var serializer = Activator.CreateInstance(serpentSerializerType, Config.SERPENT_INDENT, Config.SERPENT_SET_LITERALS, true);
			return (byte[]) serializeMethod.Invoke(serializer, new object[] {invokeparams});
		}
		
		public override object deserializeData(byte[] data)
		{
			// call the "Parse" method, using reflection
			var parser = Activator.CreateInstance(serpentParserType);
			var ast = parseMethod.Invoke(parser, new object[] {data});
			// call the "GetData" method on the Ast, using reflection
			Func<IDictionary, object> dictToInstanceFunc = DictToInstance;
			return astGetDataMethod.Invoke(ast, new object[] {dictToInstanceFunc});
		}
		
		/**
		 * Utility function to convert obj back to actual bytes if it is a serpent-encoded bytes dictionary
		 * (a IDictionary with base-64 encoded 'data' in it and 'encoding'='base64').
		 * If obj is already a byte array, return obj unmodified.
		 * If it is something else, throw an IllegalArgumentException
		 * (implementation used of net.razorvine.serpent.Parser)
		 */
		public static byte[] ToBytes(object obj) {
			try {
				return (byte[]) tobytesMethod.Invoke(null, new [] {obj});
			} catch(TargetInvocationException x) {
				if(x.InnerException != null)
					throw x.InnerException;
				throw;
			}
		}		
	}
}
