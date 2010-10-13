//
// parameter.cs: Parameter definition.
//
// Author: Miguel de Icaza (miguel@gnu.org)
//         Marek Safar (marek.safar@seznam.cz)
//
// Dual licensed under the terms of the MIT X11 or GNU GPL
//
// Copyright 2001-2003 Ximian, Inc (http://www.ximian.com)
// Copyright 2003-2008 Novell, Inc. 
//
//
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Linq;

namespace Mono.CSharp {

	/// <summary>
	///   Abstract Base class for parameters of a method.
	/// </summary>
	public abstract class ParameterBase : Attributable
	{
		protected ParameterBuilder builder;

		public override void ApplyAttributeBuilder (Attribute a, MethodSpec ctor, byte[] cdata, PredefinedAttributes pa)
		{
#if false
			if (a.Type == pa.MarshalAs) {
				UnmanagedMarshal marshal = a.GetMarshal (this);
				if (marshal != null) {
					builder.SetMarshal (marshal);
				}
				return;
			}
#endif
			if (a.HasSecurityAttribute) {
				a.Error_InvalidSecurityParent ();
				return;
			}

			if (a.Type == pa.Dynamic) {
				a.Error_MisusedDynamicAttribute ();
				return;
			}

			builder.SetCustomAttribute ((ConstructorInfo) ctor.GetMetaInfo (), cdata);
		}

		public ParameterBuilder Builder {
			get {
				return builder;
			}
		}

		public override bool IsClsComplianceRequired()
		{
			return false;
		}
	}

	/// <summary>
	/// Class for applying custom attributes on the return type
	/// </summary>
	public class ReturnParameter : ParameterBase
	{
		MemberCore method;

		// TODO: merge method and mb
		public ReturnParameter (MemberCore method, MethodBuilder mb, Location location)
		{
			this.method = method;
			try {
				builder = mb.DefineParameter (0, ParameterAttributes.None, "");			
			}
			catch (ArgumentOutOfRangeException) {
				method.Compiler.Report.RuntimeMissingSupport (location, "custom attributes on the return type");
			}
		}

		public override void ApplyAttributeBuilder (Attribute a, MethodSpec ctor, byte[] cdata, PredefinedAttributes pa)
		{
			if (a.Type == pa.CLSCompliant) {
				method.Compiler.Report.Warning (3023, 1, a.Location,
					"CLSCompliant attribute has no meaning when applied to return types. Try putting it on the method instead");
			}

			// This occurs after Warning -28
			if (builder == null)
				return;

			base.ApplyAttributeBuilder (a, ctor, cdata, pa);
		}

		public override AttributeTargets AttributeTargets {
			get {
				return AttributeTargets.ReturnValue;
			}
		}

		/// <summary>
		/// Is never called
		/// </summary>
		public override string[] ValidAttributeTargets {
			get {
				return null;
			}
		}
	}

	/// <summary>
	/// Class for applying custom attributes on the implicit parameter type
	/// of the 'set' method in properties, and the 'add' and 'remove' methods in events.
	/// </summary>
	/// 
	// TODO: should use more code from Parameter.ApplyAttributeBuilder
	public class ImplicitParameter : ParameterBase {
		public ImplicitParameter (MethodBuilder mb)
		{
			builder = mb.DefineParameter (1, ParameterAttributes.None, "value");			
		}

		public override AttributeTargets AttributeTargets {
			get {
				return AttributeTargets.Parameter;
			}
		}

		/// <summary>
		/// Is never called
		/// </summary>
		public override string[] ValidAttributeTargets {
			get {
				return null;
			}
		}
	}

	public class ImplicitLambdaParameter : Parameter
	{
		public ImplicitLambdaParameter (string name, Location loc)
			: base (null, name, Modifier.NONE, null, loc)
		{
		}

		public override TypeSpec Resolve (IMemberContext ec, int index)
		{
			if (parameter_type == null)
				throw new InternalErrorException ("A type of implicit lambda parameter `{0}' is not set",
					Name);

			base.idx = index;
			return parameter_type;
		}

		public void SetParameterType (TypeSpec type)
		{
			parameter_type = type;
		}
	}

	public class ParamsParameter : Parameter {
		public ParamsParameter (FullNamedExpression type, string name, Attributes attrs, Location loc):
			base (type, name, Parameter.Modifier.PARAMS, attrs, loc)
		{
		}

		public override TypeSpec Resolve (IMemberContext ec, int index)
		{
			if (base.Resolve (ec, index) == null)
				return null;

			var ac = parameter_type as ArrayContainer;
			if (ac == null || ac.Rank != 1) {
				ec.Compiler.Report.Error (225, Location, "The params parameter must be a single dimensional array");
				return null;
			}

			return parameter_type;
		}

		public override void ApplyAttributes (MethodBuilder mb, ConstructorBuilder cb, int index, PredefinedAttributes pa)
		{
			base.ApplyAttributes (mb, cb, index, pa);
			pa.ParamArray.EmitAttribute (builder);
		}
	}

	public class ArglistParameter : Parameter {
		// Doesn't have proper type because it's never chosen for better conversion
		public ArglistParameter (Location loc) :
			base (null, String.Empty, Parameter.Modifier.NONE, null, loc)
		{
			parameter_type = InternalType.Arglist;
		}

		public override void  ApplyAttributes (MethodBuilder mb, ConstructorBuilder cb, int index, PredefinedAttributes pa)
		{
			// Nothing to do
		}

		public override bool CheckAccessibility (InterfaceMemberBase member)
		{
			return true;
		}

		public override TypeSpec Resolve (IMemberContext ec, int index)
		{
			return parameter_type;
		}
	}

	public interface IParameterData
	{
		Expression DefaultValue { get; }
		bool HasExtensionMethodModifier { get; }
		bool HasDefaultValue { get; }
		Parameter.Modifier ModFlags { get; }
		string Name { get; }
	}

	//
	// Parameter information created by parser
	//
	public class Parameter : ParameterBase, IParameterData, ILocalVariable // TODO: INamedBlockVariable
	{
		[Flags]
		public enum Modifier : byte {
			NONE    = 0,
			REF     = REFMASK | ISBYREF,
			OUT     = OUTMASK | ISBYREF,
			PARAMS  = 4,
			// This is a flag which says that it's either REF or OUT.
			ISBYREF = 8,
			REFMASK	= 32,
			OUTMASK = 64,
			This	= 128
		}

		static string[] attribute_targets = new string [] { "param" };

		FullNamedExpression texpr;
		readonly Modifier modFlags;
		string name;
		Expression default_expr;
		protected TypeSpec parameter_type;
		readonly Location loc;
		protected int idx;
		public bool HasAddressTaken;

		TemporaryVariableReference expr_tree_variable;
		static TypeExpr parameter_expr_tree_type;

		HoistedVariable hoisted_variant;

		public Parameter (FullNamedExpression type, string name, Modifier mod, Attributes attrs, Location loc)
		{
			this.name = name;
			modFlags = mod;
			this.loc = loc;
			texpr = type;

			// Only assign, attributes will be attached during resolve
			base.attributes = attrs;
		}

		#region Properties

		public Expression DefaultValue {
			get {
				return default_expr;
			}
			set {
				default_expr = value;
			}
		}

		public Location Location {
			get {
				return loc;
			}
		}

		public TypeSpec Type {
			get {
				return parameter_type;
			}
		}

		public FullNamedExpression TypeExpression  {
			get {
				return texpr;
			}
		}

		public override string[] ValidAttributeTargets {
			get {
				return attribute_targets;
			}
		}

		#endregion

		public override void ApplyAttributeBuilder (Attribute a, MethodSpec ctor, byte[] cdata, PredefinedAttributes pa)
		{
			if (a.Type == pa.In && ModFlags == Modifier.OUT) {
				a.Report.Error (36, a.Location, "An out parameter cannot have the `In' attribute");
				return;
			}

			if (a.Type == pa.ParamArray) {
				a.Report.Error (674, a.Location, "Do not use `System.ParamArrayAttribute'. Use the `params' keyword instead");
				return;
			}

			if (a.Type == pa.Out && (ModFlags & Modifier.REF) == Modifier.REF &&
			    !OptAttributes.Contains (pa.In)) {
				a.Report.Error (662, a.Location,
					"Cannot specify only `Out' attribute on a ref parameter. Use both `In' and `Out' attributes or neither");
				return;
			}

			if (a.Type == pa.CLSCompliant) {
				a.Report.Warning (3022, 1, a.Location, "CLSCompliant attribute has no meaning when applied to parameters. Try putting it on the method instead");
			}

			if (HasDefaultValue && (a.Type == pa.DefaultParameterValue || a.Type == pa.OptionalParameter)) {
				a.Report.Error (1745, a.Location,
					"Cannot specify `{0}' attribute on optional parameter `{1}'",
					TypeManager.CSharpName (a.Type).Replace ("Attribute", ""), Name);
				return;
			}

			if (a.Type == pa.DefaultParameterValue) {
				TypeSpec arg_type;
				var c = a.GetParameterDefaultValue (out arg_type);
				if (c == null) {
					if (parameter_type == TypeManager.object_type) {
						a.Report.Error (1910, a.Location, "Argument of type `{0}' is not applicable for the DefaultParameterValue attribute",
							arg_type.GetSignatureForError ());
					} else {
						a.Report.Error (1909, a.Location, "The DefaultParameterValue attribute is not applicable on parameters of type `{0}'",
							parameter_type.GetSignatureForError ()); ;
					}

					return;
				}

				if (arg_type == parameter_type || parameter_type == TypeManager.object_type || 
					(c.IsNull && TypeManager.IsReferenceType (parameter_type) && !TypeManager.IsGenericParameter (parameter_type)))
					builder.SetConstant (c.GetValue ());
				else
					a.Report.Error (1908, a.Location, "The type of the default value should match the type of the parameter");

				return;
			}

			base.ApplyAttributeBuilder (a, ctor, cdata, pa);
		}
		
		public virtual bool CheckAccessibility (InterfaceMemberBase member)
		{
			if (parameter_type == null)
				return true;

			return member.IsAccessibleAs (parameter_type);
		}

		public static void Reset ()
		{
			parameter_expr_tree_type = null;
		}

		// <summary>
		//   Resolve is used in method definitions
		// </summary>
		public virtual TypeSpec Resolve (IMemberContext rc, int index)
		{
			if (parameter_type != null)
				return parameter_type;

			if (attributes != null)
				attributes.AttachTo (this, rc);

			var expr = texpr.ResolveAsTypeTerminal (rc, false);
			if (expr == null)
				return null;

			this.idx = index;
			texpr = expr;
			parameter_type = texpr.Type;
	
			if ((modFlags & Parameter.Modifier.ISBYREF) != 0 &&
				TypeManager.IsSpecialType (parameter_type)) {
				rc.Compiler.Report.Error (1601, Location, "Method or delegate parameter cannot be of type `{0}'",
					GetSignatureForError ());
				return null;
			}

			TypeManager.CheckTypeVariance (parameter_type,
				(modFlags & Parameter.Modifier.ISBYREF) != 0 ? Variance.None : Variance.Contravariant,
				rc);

			if (parameter_type.IsStatic) {
				rc.Compiler.Report.Error (721, Location, "`{0}': static types cannot be used as parameters",
					texpr.GetSignatureForError ());
				return parameter_type;
			}

			if ((modFlags & Modifier.This) != 0 && (parameter_type.IsPointer || parameter_type == InternalType.Dynamic)) {
				rc.Compiler.Report.Error (1103, Location, "The extension method cannot be of type `{0}'",
					TypeManager.CSharpName (parameter_type));
			}

			return parameter_type;
		}

		public void ResolveDefaultValue (ResolveContext rc)
		{
			//
			// Default value was specified using an expression
			//
			if (default_expr != null) {
				default_expr = ResolveDefaultExpression (rc);
				return;
			}

			if (attributes == null)
				return;
			
			var pa = attributes.Search (rc.Compiler.PredefinedAttributes.OptionalParameter);
			if (pa == null)
				return;

			//
			// Default value was specified using an attribute
			//
			attributes.Attrs.Remove (pa);

			TypeSpec expr_type = null;
			pa = attributes.Search (rc.Compiler.PredefinedAttributes.DefaultParameterValue);
			if (pa != null) {
				attributes.Attrs.Remove (pa);
				default_expr = pa.GetParameterDefaultValue (out expr_type);
			} else {
				default_expr = EmptyExpression.MissingValue;
			}

			if (default_expr == null) {
				if (expr_type == null)
					expr_type = parameter_type;

				default_expr = new DefaultValueExpression (new TypeExpression (expr_type, Location), Location);
			}

			default_expr = default_expr.Resolve (rc);
		}

		Expression ResolveDefaultExpression (ResolveContext rc)
		{
			default_expr = default_expr.Resolve (rc);
			if (default_expr == null)
				return null;

			if (!(default_expr is Constant || default_expr is DefaultValueExpression || (default_expr is New && ((New)default_expr).IsDefaultStruct))) {
				rc.Compiler.Report.Error (1736, default_expr.Location,
					"The expression being assigned to optional parameter `{0}' must be a constant or default value",
					Name);

				return null;
			}

			if (default_expr.Type == parameter_type)
				return default_expr;

			if (TypeManager.IsNullableType (parameter_type)) {
				if (default_expr.Type == InternalType.Null)
					return default_expr;

				var underlying = Nullable.NullableInfo.GetUnderlyingType (parameter_type);
				var c = New.Constantify (underlying, Location.Null);
				if (c == null) {
					rc.Compiler.Report.Error (1770, default_expr.Location,
						"The expression being assigned to nullable optional parameter `{0}' must be default value",
						Name);
					return null;
				}

				c = c.Resolve (rc);
				if (c.Type == default_expr.Type)
					return default_expr;
			} else {
				var res = Convert.ImplicitConversionStandard (rc, default_expr, parameter_type, default_expr.Location);
				if (res != null) {
					if (!default_expr.IsNull && TypeManager.IsReferenceType (parameter_type) && parameter_type != TypeManager.string_type) {
						rc.Compiler.Report.Error (1763, default_expr.Location,
							"Optional parameter `{0}' of type `{1}' can only be initialized with `null'",
							Name, GetSignatureForError ());

						return null;
					}

					return res;
				}
			}

			rc.Compiler.Report.Error (1750, Location,
				"Optional parameter expression of type `{0}' cannot be converted to parameter type `{1}'",
				TypeManager.CSharpName (default_expr.Type), GetSignatureForError ());

			return null;
		}

		public bool HasDefaultValue {
			get { return default_expr != null; }
		}

		public bool HasExtensionMethodModifier {
			get { return (modFlags & Modifier.This) != 0; }
		}

		//
		// Hoisted parameter variant
		//
		public HoistedVariable HoistedVariant {
			get {
				return hoisted_variant;
			}
			set {
				hoisted_variant = value;
			}
		}

		public Modifier ModFlags {
			get { return modFlags & ~Modifier.This; }
		}

		public string Name {
			get { return name; }
			set { name = value; }
		}

		ParameterAttributes Attributes {
			get { return ParametersCompiled.GetParameterAttribute (modFlags) |
				(HasDefaultValue ? ParameterAttributes.Optional : ParameterAttributes.None); }
		}

		public override AttributeTargets AttributeTargets {
			get {
				return AttributeTargets.Parameter;
			}
		}

		public virtual string GetSignatureForError ()
		{
			string type_name;
			if (parameter_type != null)
				type_name = TypeManager.CSharpName (parameter_type);
			else
				type_name = texpr.GetSignatureForError ();

			string mod = GetModifierSignature (modFlags);
			if (mod.Length > 0)
				return String.Concat (mod, " ", type_name);

			return type_name;
		}

		public static string GetModifierSignature (Modifier mod)
		{
			switch (mod) {
			case Modifier.OUT:
				return "out";
			case Modifier.PARAMS:
				return "params";
			case Modifier.REF:
				return "ref";
			case Modifier.This:
				return "this";
			default:
				return "";
			}
		}

		public void IsClsCompliant (IMemberContext ctx)
		{
			if (parameter_type.IsCLSCompliant ())
				return;

			ctx.Compiler.Report.Warning (3001, 1, Location,
				"Argument type `{0}' is not CLS-compliant", GetSignatureForError ());
		}

		public virtual void ApplyAttributes (MethodBuilder mb, ConstructorBuilder cb, int index, PredefinedAttributes pa)
		{
			if (mb == null)
				builder = cb.DefineParameter (index, Attributes, Name);
			else
				builder = mb.DefineParameter (index, Attributes, Name);

			if (OptAttributes != null)
				OptAttributes.Emit ();

			if (HasDefaultValue) {
				//
				// Emit constant values for true constants only, the other
				// constant-like expressions will rely on default value expression
				//
				Constant c = default_expr as Constant;
				if (c != null) {
					if (default_expr.Type == TypeManager.decimal_type) {
						builder.SetCustomAttribute (Const.CreateDecimalConstantAttribute (c, pa));
					} else {
						builder.SetConstant (c.GetTypedValue ());
					}
				}
			}

			if (parameter_type != null) {
				if (parameter_type == InternalType.Dynamic) {
					pa.Dynamic.EmitAttribute (builder);
				} else if (parameter_type.HasDynamicElement) {
					pa.Dynamic.EmitAttribute (builder, parameter_type);
				}
			}
		}

		public Parameter Clone ()
		{
			Parameter p = (Parameter) MemberwiseClone ();
			if (attributes != null)
				p.attributes = attributes.Clone ();

			return p;
		}

		public ExpressionStatement CreateExpressionTreeVariable (BlockContext ec)
		{
			if ((modFlags & Modifier.ISBYREF) != 0)
				ec.Report.Error (1951, Location, "An expression tree parameter cannot use `ref' or `out' modifier");

			expr_tree_variable = TemporaryVariableReference.Create (ResolveParameterExpressionType (ec, Location).Type, ec.CurrentBlock.ParametersBlock, Location);
			expr_tree_variable = (TemporaryVariableReference) expr_tree_variable.Resolve (ec);

			Arguments arguments = new Arguments (2);
			arguments.Add (new Argument (new TypeOf (
				new TypeExpression (parameter_type, Location), Location)));
			arguments.Add (new Argument (new StringConstant (Name, Location)));
			return new SimpleAssign (ExpressionTreeVariableReference (),
				Expression.CreateExpressionFactoryCall (ec, "Parameter", null, arguments, Location));
		}

		public void Emit (EmitContext ec)
		{
			int arg_idx = idx;
			if (!ec.IsStatic)
				arg_idx++;

			ParameterReference.EmitLdArg (ec, arg_idx);
		}

		public void EmitAssign (EmitContext ec)
		{
			int arg_idx = idx;
			if (!ec.IsStatic)
				arg_idx++;

			if (arg_idx <= 255)
				ec.Emit (OpCodes.Starg_S, (byte) arg_idx);
			else
				ec.Emit (OpCodes.Starg, arg_idx);
		}

		public void EmitAddressOf (EmitContext ec)
		{
			int arg_idx = idx;

			if (!ec.IsStatic)
				arg_idx++;

			bool is_ref = (ModFlags & Modifier.ISBYREF) != 0;
			if (is_ref) {
				ParameterReference.EmitLdArg (ec, arg_idx);
			} else {
				if (arg_idx <= 255)
					ec.Emit (OpCodes.Ldarga_S, (byte) arg_idx);
				else
					ec.Emit (OpCodes.Ldarga, arg_idx);
			}
		}

		public TemporaryVariableReference ExpressionTreeVariableReference ()
		{
			return expr_tree_variable;
		}

		//
		// System.Linq.Expressions.ParameterExpression type
		//
		public static TypeExpr ResolveParameterExpressionType (IMemberContext ec, Location location)
		{
			if (parameter_expr_tree_type != null)
				return parameter_expr_tree_type;

			TypeSpec p_type = TypeManager.parameter_expression_type;
			if (p_type == null) {
				p_type = TypeManager.CoreLookupType (ec.Compiler, "System.Linq.Expressions", "ParameterExpression", MemberKind.Class, true);
				TypeManager.parameter_expression_type = p_type;
			}

			parameter_expr_tree_type = new TypeExpression (p_type, location).
				ResolveAsTypeTerminal (ec, false);

			return parameter_expr_tree_type;
		}

		public void Warning_UselessOptionalParameter (Report Report)
		{
			Report.Warning (1066, 1, Location,
				"The default value specified for optional parameter `{0}' will never be used",
				Name);
		}
	}

	//
	// Imported or resolved parameter information
	//
	public class ParameterData : IParameterData
	{
		readonly string name;
		readonly Parameter.Modifier modifiers;
		readonly Expression default_value;

		public ParameterData (string name, Parameter.Modifier modifiers)
		{
			this.name = name;
			this.modifiers = modifiers;
		}

		public ParameterData (string name, Parameter.Modifier modifiers, Expression defaultValue)
			: this (name, modifiers)
		{
			this.default_value = defaultValue;
		}

		#region IParameterData Members

		public Expression DefaultValue {
			get { return default_value; }
		}

		public bool HasExtensionMethodModifier {
			get { return (modifiers & Parameter.Modifier.This) != 0; }
		}

		public bool HasDefaultValue {
			get { return default_value != null; }
		}

		public Parameter.Modifier ModFlags {
			get { return modifiers & ~Parameter.Modifier.This; }
		}

		public string Name {
			get { return name; }
		}

		#endregion
	}

	public abstract class AParametersCollection
	{
		protected bool has_arglist;
		protected bool has_params;

		// Null object pattern
		protected IParameterData [] parameters;
		protected TypeSpec [] types;

		public CallingConventions CallingConvention {
			get {
				return has_arglist ?
					CallingConventions.VarArgs :
					CallingConventions.Standard;
			}
		}

		public int Count {
			get { return parameters.Length; }
		}

		public TypeSpec ExtensionMethodType {
			get {
				if (Count == 0)
					return null;

				return FixedParameters [0].HasExtensionMethodModifier ?
					types [0] : null;
			}
		}

		public IParameterData [] FixedParameters {
			get {
				return parameters;
			}
		}

		public static ParameterAttributes GetParameterAttribute (Parameter.Modifier modFlags)
		{
			return (modFlags & Parameter.Modifier.OUT) == Parameter.Modifier.OUT ?
				ParameterAttributes.Out : ParameterAttributes.None;
		}

		// Very expensive operation
		public Type[] GetMetaInfo ()
		{
			Type[] types;
			if (has_arglist) {
				if (Count == 1)
					return Type.EmptyTypes;

				types = new Type [Count - 1];
			} else {
				if (Count == 0)
					return Type.EmptyTypes;

				types = new Type [Count];
			}

			for (int i = 0; i < types.Length; ++i) {
				types[i] = Types[i].GetMetaInfo ();

				if ((FixedParameters [i].ModFlags & Parameter.Modifier.ISBYREF) == 0)
					continue;

				// TODO MemberCache: Should go to MetaInfo getter
				types [i] = types [i].MakeByRefType ();
			}

			return types;
		}

		//
		// Returns the parameter information based on the name
		//
		public int GetParameterIndexByName (string name)
		{
			for (int idx = 0; idx < Count; ++idx) {
				if (parameters [idx].Name == name)
					return idx;
			}

			return -1;
		}

		public string GetSignatureForError ()
		{
			return GetSignatureForError ("(", ")", Count);
		}

		public string GetSignatureForError (string start, string end, int count)
		{
			StringBuilder sb = new StringBuilder (start);
			for (int i = 0; i < count; ++i) {
				if (i != 0)
					sb.Append (", ");
				sb.Append (ParameterDesc (i));
			}
			sb.Append (end);
			return sb.ToString ();
		}

		public bool HasArglist {
			get { return has_arglist; }
		}

		public bool HasExtensionMethodType {
			get {
				if (Count == 0)
					return false;

				return FixedParameters [0].HasExtensionMethodModifier;
			}
		}

		public bool HasParams {
			get { return has_params; }
		}

		public bool IsEmpty {
			get { return parameters.Length == 0; }
		}

		public AParametersCollection Inflate (TypeParameterInflator inflator)
		{
			TypeSpec[] inflated_types = null;
			bool default_value = false;

			for (int i = 0; i < Count; ++i) {
				var inflated_param = inflator.Inflate (types[i]);
				if (inflated_types == null) {
					if (inflated_param == types[i])
						continue;

					default_value |= FixedParameters[i] is DefaultValueExpression;
					inflated_types = new TypeSpec[types.Length];
					Array.Copy (types, inflated_types, types.Length);	
				}

				inflated_types[i] = inflated_param;
			}

			if (inflated_types == null)
				return this;

			var clone = (AParametersCollection) MemberwiseClone ();
			clone.types = inflated_types;
			if (default_value) {
				for (int i = 0; i < Count; ++i) {
					var dve = clone.FixedParameters[i] as DefaultValueExpression;
					if (dve != null) {
						throw new NotImplementedException ("net");
						//	clone.FixedParameters [i].DefaultValue = new DefaultValueExpression ();
					}
				}
			}

			return clone;
		}

		public string ParameterDesc (int pos)
		{
			if (types == null || types [pos] == null)
				return ((Parameter)FixedParameters [pos]).GetSignatureForError ();

			string type = TypeManager.CSharpName (types [pos]);
			if (FixedParameters [pos].HasExtensionMethodModifier)
				return "this " + type;

			Parameter.Modifier mod = FixedParameters [pos].ModFlags;
			if (mod == 0)
				return type;

			return Parameter.GetModifierSignature (mod) + " " + type;
		}

		public TypeSpec[] Types {
			get { return types; }
			set { types = value; }
		}
	}

	//
	// A collection of imported or resolved parameters
	//
	public class ParametersImported : AParametersCollection
	{
		public ParametersImported (IParameterData [] parameters, TypeSpec [] types, bool hasArglist, bool hasParams)
		{
			this.parameters = parameters;
			this.types = types;
			this.has_arglist = hasArglist;
			this.has_params = hasParams;
		}

		public ParametersImported (IParameterData[] param, TypeSpec[] types, bool hasParams)
		{
			this.parameters = param;
			this.types = types;
			this.has_params = hasParams;
		}
	}

	/// <summary>
	///   Represents the methods parameters
	/// </summary>
	public class ParametersCompiled : AParametersCollection
	{
		public static readonly ParametersCompiled EmptyReadOnlyParameters = new ParametersCompiled ();
		
		// Used by C# 2.0 delegates
		public static readonly ParametersCompiled Undefined = new ParametersCompiled ();

		private ParametersCompiled ()
		{
			parameters = new Parameter [0];
			types = TypeSpec.EmptyTypes;
		}

		private ParametersCompiled (IParameterData[] parameters, TypeSpec[] types)
		{
			this.parameters = parameters;
		    this.types = types;
		}
		
		public ParametersCompiled (params Parameter[] parameters)
		{
			if (parameters == null || parameters.Length == 0)
				throw new ArgumentException ("Use EmptyReadOnlyParameters");

			this.parameters = parameters;
			int count = parameters.Length;

			for (int i = 0; i < count; i++){
				has_params |= (parameters [i].ModFlags & Parameter.Modifier.PARAMS) != 0;
			}
		}

		public ParametersCompiled (Parameter [] parameters, bool has_arglist) :
			this (parameters)
		{
			this.has_arglist = has_arglist;
		}
		
		public static ParametersCompiled CreateFullyResolved (Parameter p, TypeSpec type)
		{
			return new ParametersCompiled (new Parameter [] { p }, new TypeSpec [] { type });
		}
		
		public static ParametersCompiled CreateFullyResolved (Parameter[] parameters, TypeSpec[] types)
		{
			return new ParametersCompiled (parameters, types);
		}

		//
		// TODO: This does not fit here, it should go to different version of AParametersCollection
		// as the underlying type is not Parameter and some methods will fail to cast
		//
		public static AParametersCollection CreateFullyResolved (TypeSpec[] types)
		{
			var pd = new ParameterData [types.Length];
			for (int i = 0; i < pd.Length; ++i)
				pd[i] = new ParameterData (null, Parameter.Modifier.NONE, null);

			return new ParametersCompiled (pd, types);
		}

		public static ParametersCompiled CreateImplicitParameter (FullNamedExpression texpr, Location loc)
		{
			return new ParametersCompiled (
				new[] { new Parameter (texpr, "value", Parameter.Modifier.NONE, null, loc) },
				null);
		}

		//
		// Returns non-zero value for equal CLS parameter signatures
		//
		public static int IsSameClsSignature (AParametersCollection a, AParametersCollection b)
		{
			int res = 0;

			for (int i = 0; i < a.Count; ++i) {
				var a_type = a.Types[i];
				var b_type = b.Types[i];
				if (TypeSpecComparer.Override.IsEqual (a_type, b_type)) {
					const Parameter.Modifier ref_out = Parameter.Modifier.REF | Parameter.Modifier.OUT;
					if ((a.FixedParameters[i].ModFlags & ref_out) != (b.FixedParameters[i].ModFlags & ref_out))
						res |= 1;

					continue;
				}

				var ac_a = a_type as ArrayContainer;
				if (ac_a == null)
					return 0;

				var ac_b = b_type as ArrayContainer;
				if (ac_b == null)
					return 0;

				if (ac_a.Element is ArrayContainer || ac_b.Element is ArrayContainer) {
					res |= 2;
					continue;
				}

				if (ac_a.Rank != ac_b.Rank && TypeSpecComparer.Override.IsEqual (ac_a.Element, ac_b.Element)) {
					res |= 1;
					continue;
				}

				return 0;
			}

			return res;
		}

		public static ParametersCompiled MergeGenerated (CompilerContext ctx, ParametersCompiled userParams, bool checkConflicts, Parameter compilerParams, TypeSpec compilerTypes)
		{
			return MergeGenerated (ctx, userParams, checkConflicts,
				new Parameter [] { compilerParams },
				new TypeSpec [] { compilerTypes });
		}

		//
		// Use this method when you merge compiler generated parameters with user parameters
		//
		public static ParametersCompiled MergeGenerated (CompilerContext ctx, ParametersCompiled userParams, bool checkConflicts, Parameter[] compilerParams, TypeSpec[] compilerTypes)
		{
			Parameter[] all_params = new Parameter [userParams.Count + compilerParams.Length];
			userParams.FixedParameters.CopyTo(all_params, 0);

			TypeSpec [] all_types;
			if (userParams.types != null) {
				all_types = new TypeSpec [all_params.Length];
				userParams.Types.CopyTo (all_types, 0);
			} else {
				all_types = null;
			}

			int last_filled = userParams.Count;
			int index = 0;
			foreach (Parameter p in compilerParams) {
				for (int i = 0; i < last_filled; ++i) {
					while (p.Name == all_params [i].Name) {
						if (checkConflicts && i < userParams.Count) {
							ctx.Report.Error (316, userParams[i].Location,
								"The parameter name `{0}' conflicts with a compiler generated name", p.Name);
						}
						p.Name = '_' + p.Name;
					}
				}
				all_params [last_filled] = p;
				if (all_types != null)
					all_types [last_filled] = compilerTypes [index++];
				++last_filled;
			}
			
			ParametersCompiled parameters = new ParametersCompiled (all_params, all_types);
			parameters.has_params = userParams.has_params;
			return parameters;
		}

		public bool Resolve (IMemberContext ec)
		{
			if (types != null)
				return true;
			
			types = new TypeSpec [Count];
			
			bool ok = true;
			Parameter p;
			for (int i = 0; i < FixedParameters.Length; ++i) {
				p = this [i];
				TypeSpec t = p.Resolve (ec, i);
				if (t == null) {
					ok = false;
					continue;
				}

				types [i] = t;
			}

			return ok;
		}

		public void ResolveDefaultValues (MemberCore m)
		{
			var count = parameters.Length;

			//
			// Try not to enter default values resolution if there are not any
			//
			if (parameters[count - 1].HasDefaultValue || (HasParams && count > 1 && parameters[count - 2].HasDefaultValue) ||
				((Parameter) parameters[count - 1]).OptAttributes != null) {
				var rc = new ResolveContext (m);
				for (int i = 0; i < count; ++i) {
					this [i].ResolveDefaultValue (rc);
				}
			}
		}

		// Define each type attribute (in/out/ref) and
		// the argument names.
		public void ApplyAttributes (IMemberContext mc, MethodBase builder)
		{
			if (Count == 0)
				return;

			MethodBuilder mb = builder as MethodBuilder;
			ConstructorBuilder cb = builder as ConstructorBuilder;
			var pa = mc.Compiler.PredefinedAttributes;

			for (int i = 0; i < Count; i++) {
				this [i].ApplyAttributes (mb, cb, i + 1, pa);
			}
		}

		public void VerifyClsCompliance (IMemberContext ctx)
		{
			foreach (Parameter p in FixedParameters)
				p.IsClsCompliant (ctx);
		}

		public Parameter this [int pos] {
			get { return (Parameter) parameters [pos]; }
		}

		public Expression CreateExpressionTree (BlockContext ec, Location loc)
		{
			var initializers = new ArrayInitializer (Count, loc);
			foreach (Parameter p in FixedParameters) {
				//
				// Each parameter expression is stored to local variable
				// to save some memory when referenced later.
				//
				StatementExpression se = new StatementExpression (p.CreateExpressionTreeVariable (ec));
				if (se.Resolve (ec)) {
					ec.CurrentBlock.AddScopeStatement (new TemporaryVariableReference.Declarator (p.ExpressionTreeVariableReference ()));
					ec.CurrentBlock.AddScopeStatement (se);
				}
				
				initializers.Add (p.ExpressionTreeVariableReference ());
			}

			return new ArrayCreation (
				Parameter.ResolveParameterExpressionType (ec, loc),
				initializers, loc);
		}

		public ParametersCompiled Clone ()
		{
			ParametersCompiled p = (ParametersCompiled) MemberwiseClone ();

			p.parameters = new IParameterData [parameters.Length];
			for (int i = 0; i < Count; ++i)
				p.parameters [i] = this [i].Clone ();

			return p;
		}
	}
}
