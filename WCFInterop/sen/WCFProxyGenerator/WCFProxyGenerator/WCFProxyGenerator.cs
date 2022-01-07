using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection.Emit;
using System.ServiceModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.ServiceModel.Description;
using System.Xml.Linq;
using System.ServiceModel.Channels;
using System.ServiceModel.Web;
using System.IO;
using System.Text;


namespace WCFProxyGenerator
{
    public class DelegateProxy
    {
        public delegate Delegate bindDelegateCallback(string name, string delegateType, string delegateDefinedInAssembly);
        public event bindDelegateCallback BindDelegate;
        public Proxy BuildProxy(string xmlDecription)
        {
            XElement doc = XElement.Parse(xmlDecription);
            foreach (var componentInterface in doc.Elements())
            {
                if (componentInterface is XContainer)
                {
                    Proxy componentProxy = new Proxy(componentInterface.Attribute("name").Value);
                    AssemblyName assembly;
                    AssemblyBuilder assemblyBuilder;
                    ModuleBuilder modbuilder;

                    assembly = new AssemblyName(componentInterface.Attribute("name").Value + "delegates");
                    assembly.Version = new Version(1, 0, 0, 0);
                    assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assembly, AssemblyBuilderAccess.Save);
                    modbuilder = assemblyBuilder.DefineDynamicModule(assembly.Name, assembly.Name + ".dll", true);


                    foreach (var method in ((XContainer)componentInterface).Elements())
                    {
                        TypeBuilder typeBuilder;
                        MethodBuilder methodBuilder;
                        string methodName = method.Attribute("name").Value;

                        typeBuilder = modbuilder.DefineType(methodName, TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoClass, typeof(System.MulticastDelegate));
                        ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public, CallingConventions.Standard, new Type[] { typeof(object), typeof(System.IntPtr) });
                        constructorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                        
                        if (method is XContainer)
                        {
                            List<Type> types = new List<Type>();
                            Type returnType = typeof(void);
                            foreach (var param in ((XContainer)method).Elements())
                            {
                                if (param.Name == "methodresult")
                                    returnType = Type.GetType(param.Attribute("type").Value);
                                else
                                    types.Add(Type.GetType(param.Attribute("type").Value));
                            }

                            methodBuilder = typeBuilder.DefineMethod("Invoke", MethodAttributes.Public, returnType, types.ToArray());
                        
                            int paramIndex = 1;
                            foreach (var param in ((XContainer)method).Elements())
                            {
                                if (param.Name != "methodresult")
                                {
                                    ParameterBuilder paramBuilder = methodBuilder.DefineParameter(paramIndex, ParameterAttributes.In, param.Attribute("name").Value);
                                    paramIndex++;
                                }
                            }
                        }
                        else
                        {
                            methodBuilder = typeBuilder.DefineMethod("Invoke", MethodAttributes.Public, typeof(void), System.Type.EmptyTypes);
                        }
                        methodBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                        Type ty = typeBuilder.CreateType();
                    }
                    assemblyBuilder.Save(assembly.Name + ".dll");
                    foreach(Type ty in Assembly.LoadFrom(assembly.Name + ".dll").GetTypes())
                    {
                        componentProxy.AddMethod(ty.Name, BindDelegate(ty.Name, ty.FullName, assembly.Name + ".dll"));
                    }
                    return componentProxy;
                }


            }
            return null;
        }
    }
    public class Proxy
    {
        private Dictionary<string, Delegate> _delegates = new Dictionary<string, Delegate>();
        private string _interfaceName;
        public Proxy(string InterfaceName)
        {
            _interfaceName = InterfaceName;
        }
        public void AddMethod(string publicName, Delegate method)
        {
            _delegates.Add(publicName, method);
        }
        public Type BuildProxyType()
        {
            AssemblyBuilder asmBuilder;
            AssemblyName assemblyName = new AssemblyName();
            assemblyName.Name = _interfaceName;
            AppDomain domain = Thread.GetDomain();
            
            asmBuilder = domain.DefineDynamicAssembly(assemblyName, 
                AssemblyBuilderAccess.RunAndSave);
            
            ModuleBuilder modBuilder = asmBuilder.DefineDynamicModule(
                asmBuilder.GetName().Name, assemblyName + ".dll", false);
            
            TypeBuilder typeBuilder = modBuilder.DefineType(_interfaceName);

            ConstructorInfo serviceContractAttr = typeof(ServiceContractAttribute).
                GetConstructor(System.Type.EmptyTypes);

            ConstructorInfo serviceBehaviorAttr = typeof(ServiceBehaviorAttribute).
                GetConstructor(System.Type.EmptyTypes);

            PropertyInfo serviceBehaviorProperty = typeof(ServiceBehaviorAttribute).
                GetProperty("InstanceContextMode");
            
            PropertyInfo serviceAttrProperty = typeof(ServiceContractAttribute).
                GetProperty("Name");

            SetTypeAttribute(typeBuilder, serviceContractAttr, 
                serviceAttrProperty, _interfaceName);

            SetTypeAttribute(typeBuilder, serviceBehaviorAttr, 
                serviceBehaviorProperty, InstanceContextMode.Single);

            FieldBuilder delgateArrayFieldBuilder = typeBuilder.DefineField(
                "_delegates", typeof(Delegate).MakeArrayType(), 
                FieldAttributes.Private);

            ConstructorBuilder constructor = typeBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.HasThis, 
                new Type[] { typeof(Delegate).MakeArrayType() });

            ILGenerator ctorGen = constructor.GetILGenerator();
            ctorGen.Emit(OpCodes.Ldarg_0);
            ctorGen.Emit(OpCodes.Ldarg_1);
            ctorGen.Emit(OpCodes.Stfld, delgateArrayFieldBuilder);
            ctorGen.Emit(OpCodes.Ret);

            int methodCounter = 0;
            MethodInfo dynInvoke = typeof(Delegate).GetMethod("DynamicInvoke",
                                        new Type[] { typeof(Object[]) });

            ConstructorInfo opContractAttr = typeof(OperationContractAttribute).
                GetConstructor(System.Type.EmptyTypes);

            foreach (var method in _delegates)
            {
                var paramInfoArr = method.Value.Method.GetParameters();
                Type[] typeParameters = new Type[paramInfoArr.Length];
                for (int i = 0; i < paramInfoArr.Length; i++)
                {
                    typeParameters[i] = paramInfoArr[i].ParameterType;
                }

                MethodBuilder methodBuilder = typeBuilder.DefineMethod(method.Key, 
                    System.Reflection.MethodAttributes.Public, 
                    method.Value.Method.ReturnType, typeParameters);

                for (int i = 0; i < paramInfoArr.Length; i++)
                {
                    methodBuilder.DefineParameter(
                        i + 1, ParameterAttributes.In, "a" + i.ToString());
                }
                
                PropertyInfo attrProperty = typeof(OperationContractAttribute).GetProperty("Name");

                methodBuilder.SetCustomAttribute(
                    new CustomAttributeBuilder(opContractAttr, new Type[] { },
                    new PropertyInfo[] { attrProperty }, new Object[] { method.Key }));


                ILGenerator gen = methodBuilder.GetILGenerator();
                gen.DeclareLocal(typeof(Delegate));
                gen.DeclareLocal(typeof(object[]));

                // Writing body

                gen.Emit(OpCodes.Ldarg_0);
                gen.Emit(OpCodes.Ldfld, delgateArrayFieldBuilder);
                gen.Emit(OpCodes.Ldc_I4, methodCounter);
                gen.Emit(OpCodes.Ldelem_Ref);
                gen.Emit(OpCodes.Stloc_0);

                gen.Emit(OpCodes.Ldc_I4, typeParameters.Length);
                gen.Emit(OpCodes.Newarr, typeof(Object));
                gen.Emit(OpCodes.Stloc_1);

                for (int i = 0; i < typeParameters.Length; i++)
                {
                    gen.Emit(OpCodes.Ldloc_1);
                    gen.Emit(OpCodes.Ldc_I4, i);
                    gen.Emit(OpCodes.Ldarg, i + 1);
                    if (typeParameters[i].IsValueType)
                        gen.Emit(OpCodes.Box, typeParameters[i]);
                    gen.Emit(OpCodes.Stelem_Ref);
                }
                gen.Emit(OpCodes.Ldloc_0);
                gen.Emit(OpCodes.Ldloc_1);
                gen.Emit(OpCodes.Callvirt, dynInvoke);

                if (method.Value.Method.ReturnType != typeof(void))
                {
                    if (method.Value.Method.ReturnType.IsValueType)
                        gen.Emit(OpCodes.Unbox_Any, 
                            method.Value.Method.ReturnType);
                }
                else
                {
                    gen.Emit(OpCodes.Pop);
                }
                gen.Emit(OpCodes.Nop);
                gen.Emit(OpCodes.Ret);
                methodCounter++;
            }
            return typeBuilder.CreateType();
        }
        public void Run(Binding binding, string addr)
        {
            Type dynamicType = BuildProxyType();

            //because we dont have a default constructor for our generated type we need to make an instance 
            //and pass that in instead of letting WCF runtime do it.
            object dynamicInstance = Activator.CreateInstance(dynamicType, new object[] { _delegates.Values.ToArray() });
            ServiceHost host = new ServiceHost(dynamicInstance);
            host.AddServiceEndpoint(dynamicType, binding, addr);
            
            //this tells WCF that we intend for clients to be allowed to ask about our provided
            //services. This is needed to support the visual studio designers.
            ServiceMetadataBehavior smb = new ServiceMetadataBehavior();
            smb.HttpGetEnabled = true;
            smb.HttpGetUrl = new Uri(addr);
            host.Description.Behaviors.Add(smb);
            
            host.Open();
            
            //this service is here so we can host the policy file for silverlight(this allows cross domain scripting)
            Uri serviceAddress = new Uri(addr);
            ServiceHost policyHost = new ServiceHost(typeof(Service), new Uri(serviceAddress.Scheme + "://" + serviceAddress.Authority));
            policyHost.AddServiceEndpoint(typeof(IPolicyRetriever), new WebHttpBinding(), "").Behaviors.Add(new WebHttpBehavior());
            policyHost.Open();
        }

        private void SetTypeAttribute(TypeBuilder typeBuilder, ConstructorInfo ci, PropertyInfo prop, object obj)
        {
            typeBuilder.SetCustomAttribute(
            new CustomAttributeBuilder(ci, new Type[] { },
            new PropertyInfo[] { prop }, new Object[] { obj }));
        }
    }

    //this interface and the class that implements it exist only to allow cross domain scripting in silverlight
    [ServiceContract]
    public interface IPolicyRetriever
    {
        [OperationContract, WebGet(UriTemplate = "/clientaccesspolicy.xml")]
        Stream GetSilverlightPolicy();
    }
    public class Service :IPolicyRetriever
    {
        Stream StringToStream(string result)
        {
            WebOperationContext.Current.OutgoingResponse.ContentType = "application/xml";
            return new MemoryStream(Encoding.UTF8.GetBytes(result));
        }
        //this is a somewhat open policy and you wouldnt want to deploy something like this
        public Stream GetSilverlightPolicy()
        {
            string result = @"<?xml version=""1.0"" encoding=""utf-8""?>
            <access-policy>
                <cross-domain-access>
                    <policy>
                        <allow-from http-request-headers=""*"">
                            <domain uri=""*""/>
                        </allow-from>
                        <grant-to>
                            <resource path=""/"" include-subpaths=""true""/>
                        </grant-to>
                    </policy>
                </cross-domain-access>
            </access-policy>";
            return StringToStream(result);
        }
    }

}
