﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;

namespace ICSharpCode.ILSpy
{
#if DEBUG
	/// <summary>
	/// Represents the ILAst "language" used for debugging purposes.
	/// </summary>
	abstract class ILAstLanguage : Language
	{
		public event EventHandler StepperUpdated;

		protected virtual void OnStepperUpdated(EventArgs e = null)
		{
			StepperUpdated?.Invoke(this, e ?? new EventArgs());
		}

		public Stepper Stepper { get; set; } = new Stepper();

		readonly string name;
		
		protected ILAstLanguage(string name)
		{
			this.name = name;
		}
		
		public override string Name { get { return name; } }
		/*
		public override void DecompileMethod(MethodDefinition method, ITextOutput output, DecompilationOptions options)
		{
			if (!method.HasBody) {
				return;
			}
			
			ILAstBuilder astBuilder = new ILAstBuilder();
			ILBlock ilMethod = new ILBlock();
			DecompilerContext context = new DecompilerContext(method.Module) { CurrentType = method.DeclaringType, CurrentMethod = method };
			ilMethod.Body = astBuilder.Build(method, inlineVariables, context);
			
			if (abortBeforeStep != null) {
				new ILAstOptimizer().Optimize(context, ilMethod, abortBeforeStep.Value);
			}
			
			if (context.CurrentMethodIsAsync)
				output.WriteLine("async/await");
			
			var allVariables = ilMethod.GetSelfAndChildrenRecursive<ILExpression>().Select(e => e.Operand as ILVariable)
				.Where(v => v != null && !v.IsParameter).Distinct();
			foreach (ILVariable v in allVariables) {
				output.WriteDefinition(v.Name, v);
				if (v.Type != null) {
					output.Write(" : ");
					if (v.IsPinned)
						output.Write("pinned ");
					v.Type.WriteTo(output, ILNameSyntax.ShortTypeName);
				}
				if (v.IsGenerated) {
					output.Write(" [generated]");
				}
				output.WriteLine();
			}
			output.WriteLine();
			
			foreach (ILNode node in ilMethod.Body) {
				node.WriteTo(output);
				output.WriteLine();
			}
	}*/

		internal static IEnumerable<ILAstLanguage> GetDebugLanguages()
		{
			yield return new TypedIL();
			CSharpDecompiler decompiler = new CSharpDecompiler(ModuleDefinition.CreateModule("Dummy", ModuleKind.Dll), new DecompilerSettings());
			yield return new BlockIL(decompiler.ILTransforms.ToList());
		}
		
		public override string FileExtension {
			get {
				return ".il";
			}
		}

		public override string TypeToString(TypeReference type, bool includeNamespace, ICustomAttributeProvider typeAttributes = null)
		{
			PlainTextOutput output = new PlainTextOutput();
			type.WriteTo(output, includeNamespace ? ILNameSyntax.TypeName : ILNameSyntax.ShortTypeName);
			return output.ToString();
		}

		public override void DecompileMethod(MethodDefinition method, ITextOutput output, DecompilationOptions options)
		{
			base.DecompileMethod(method, output, options);
			new ReflectionDisassembler(output, false, options.CancellationToken).DisassembleMethodHeader(method);
			output.WriteLine();
			output.WriteLine();
		}

		class TypedIL : ILAstLanguage
		{
			public TypedIL() : base("Typed IL") {}
			
			public override void DecompileMethod(MethodDefinition method, ITextOutput output, DecompilationOptions options)
			{
				base.DecompileMethod(method, output, options);
				if (!method.HasBody)
					return;
				var typeSystem = new DecompilerTypeSystem(method.Module);
				ILReader reader = new ILReader(typeSystem);
				reader.WriteTypedIL(method.Body, output, options.CancellationToken);
			}
		}

		class BlockIL : ILAstLanguage
		{
			readonly IReadOnlyList<IILTransform> transforms;
			readonly ILAstWritingOptions writingOptions = new ILAstWritingOptions {
				UseFieldSugar = true,
				UseLogicOperationSugar = true
			};

			public BlockIL(IReadOnlyList<IILTransform> transforms) : base("ILAst")
			{
				this.transforms = transforms;
			}

			public override void DecompileMethod(MethodDefinition method, ITextOutput output, DecompilationOptions options)
			{
				base.DecompileMethod(method, output, options);
				if (!method.HasBody)
					return;
				var typeSystem = new DecompilerTypeSystem(method.Module);
				var specializingTypeSystem = typeSystem.GetSpecializingTypeSystem(new SimpleTypeResolveContext(typeSystem.Resolve(method)));
				var reader = new ILReader(specializingTypeSystem);
				reader.UseDebugSymbols = options.DecompilerSettings.UseDebugSymbols;
				ILFunction il = reader.ReadIL(method.Body, options.CancellationToken);
				ILTransformContext context = new ILTransformContext {
					Settings = options.DecompilerSettings,
					TypeSystem = typeSystem,
					CancellationToken = options.CancellationToken
				};
				context.Stepper.StepLimit = options.StepLimit;
				context.Stepper.IsDebug = options.IsDebug;
				try {
					il.RunTransforms(transforms, context);
				} catch (StepLimitReachedException) {
				} finally {
					// update stepper even if a transform crashed unexpectedly
					if (options.StepLimit == int.MaxValue) {
						Stepper = context.Stepper;
						OnStepperUpdated(new EventArgs());
					}
				}
				(output as ISmartTextOutput)?.AddUIElement(OptionsCheckBox(nameof(writingOptions.UseFieldSugar)));
				output.WriteLine();
				(output as ISmartTextOutput)?.AddUIElement(OptionsCheckBox(nameof(writingOptions.UseLogicOperationSugar)));
				output.WriteLine();
				(output as ISmartTextOutput)?.AddButton(Images.ViewCode, "Show Steps", delegate {
					DebugSteps.Show();
				});
				output.WriteLine();
				il.WriteTo(output, writingOptions);
			}

			Func<System.Windows.UIElement> OptionsCheckBox(string propertyName)
			{
				return () => {
					var checkBox = new System.Windows.Controls.CheckBox();
					checkBox.Content = propertyName;
					checkBox.Cursor = System.Windows.Input.Cursors.Arrow;
					checkBox.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,
						new System.Windows.Data.Binding(propertyName) { Source = writingOptions });
					return checkBox;
				};
			}
		}
	}
	#endif
}
