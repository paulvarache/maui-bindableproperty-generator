﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Maui.BindableProperty.Generator.Helpers;
using Maui.BindableProperty.Generator.Core.BindableProperty.Implementation;
using Maui.BindableProperty.Generator.Core.BindableProperty.Implementation.Interfaces;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;

namespace Maui.BindableProperty.Generator.Core.BindableProperty;

[Generator]
public class AutoBindablePropertyGenerator : IncrementalGeneratorBase, IIncrementalGenerator
{
    private readonly List<Type> TypeImplementations = new() {
        typeof(DefaultValue),
        typeof(PropertyChanged),
        typeof(DefaultBindingMode),
        typeof(ValidateValue)
    };
    private readonly List<IImplementation> Implementations = new();

    private const string attributeText = @"
        #pragma warning disable
        #nullable enable
        using System;
        namespace Maui.BindableProperty.Generator.Core
        {
            public enum BindablePropertyAccessibility
            {
                /// <summary>
                /// If 'Undefined', bindable property will be defined in the same way as the class that contains it.
                /// </summary>
                Undefined = 0,

                /// <summary>
                /// Bindable property will be defined as 'private'
                /// </summary>
                Private = 1,

                /// <summary>
                /// Bindable property will be defined as 'private protected'
                /// </summary>
                ProtectedAndInternal = 2,

                /// <summary>
                /// Bindable property will be defined as 'protected'
                /// </summary>
                Protected = 3,

                /// <summary>
                /// Bindable property will be defined as 'internal'
                /// </summary>
                Internal = 4,

                /// <summary>
                /// Bindable property will be defined as 'protected internal'  
                /// </summary>     
                ProtectedOrInternal = 5,

                /// <summary>
                /// Bindable property will be defined as 'public'  
                /// </summary>
                Public = 6
            }

            [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
            [System.Diagnostics.Conditional(""AutoBindableGenerator_DEBUG"")]
            public sealed class AutoBindableAttribute : Attribute
            {
                public AutoBindableAttribute(){}

                public string PropertyName { get; set; } = string.Empty;

                public string? OnChanged { get; set; }

                public string? DefaultValue { get; set; }

                public string? DefaultBindingMode { get; set; }

                public string? ValidateValue { get; set; }

                public bool HidesUnderlyingProperty { get; set; } = false;

                public BindablePropertyAccessibility PropertyAccessibility { get; set; } = BindablePropertyAccessibility.Undefined;
            }
        }";

    private string ProcessClass(
        INamedTypeSymbol classSymbol,
        List<IFieldSymbol> fields,
        ISymbol attributeSymbol,
        SourceProductionContext context)
    {
        if (!classSymbol.ContainingSymbol.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        AutoBindableConstants.ExceptionMBPG002Id,
                        AutoBindableConstants.ExceptionTitle,
                        AutoBindableConstants.ExceptionMBPG002Message,
                        AutoBindableConstants.ProjectName,
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true
                    ),
                    classSymbol.Locations.FirstOrDefault(),
                    classSymbol.ToDisplayString()
                )
            );

            return null;
        }

        var namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
        var w = new CodeWriter(CodeWriterSettings.CSharpDefault);
        w._("// <auto-generated/>");
        w._("#pragma warning disable");
        w._("#nullable enable");
        using (w.B(@$"namespace {namespaceName}"))
        {
            var classAccessibility = SyntaxFacts.GetText(classSymbol.DeclaredAccessibility);
            using (w.B(@$"{classAccessibility} partial class {classSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}"))
            {
                // Create properties for each field 
                foreach (IFieldSymbol fieldSymbol in fields)
                {
                    try
                    {
                        this.ProcessBindableProperty(w, fieldSymbol, attributeSymbol, classSymbol, context);
                    }
                    catch (Exception e)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                new DiagnosticDescriptor(
                                    AutoBindableConstants.ExceptionMBPG003Id,
                                    AutoBindableConstants.ExceptionTitle,
                                    AutoBindableConstants.ExceptionMBPG003Message,
                                    AutoBindableConstants.ProjectName,
                                    DiagnosticSeverity.Error,
                                    isEnabledByDefault: true
                                ),
                                classSymbol.Locations.FirstOrDefault(),
                                fieldSymbol.ToDisplayString(),
                                classSymbol.ToDisplayString(),
                                e.ToString()
                            )
                        );
                    }
                }
            }
        }

        return w.ToString();
    }

    private void ProcessBindableProperty(
        CodeWriter w,
        IFieldSymbol fieldSymbol,
        ISymbol attributeSymbol,
        INamedTypeSymbol classSymbol,
        SourceProductionContext context)
    {
        // Get the name and type of the field
        var fieldName = fieldSymbol.Name;
        var fieldType = fieldSymbol.Type;

        var nameProperty = fieldSymbol.GetTypedConstant(attributeSymbol, AutoBindableConstants.AttrPropertyName);
        this.InitializeImplementations(fieldSymbol, attributeSymbol, classSymbol);

        var propertyName = this.ChooseName(fieldName, nameProperty);
        if (propertyName?.Length == 0 || propertyName == fieldName)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        AutoBindableConstants.ExceptionMBPG004Id,
                        AutoBindableConstants.ExceptionTitle,
                        AutoBindableConstants.ExceptionMBPG004Message,
                        AutoBindableConstants.ProjectName,
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true
                    ),
                    classSymbol.Locations.FirstOrDefault(),
                    fieldName
                )
            );
            return;
        }

        var bindablePropertyName = $@"{propertyName}Property";
        var customParameters = this.ProcessBindableParameters();
        var applyHidesUnderlying = fieldSymbol.GetValue<bool>(attributeSymbol, AutoBindableConstants.AttrHidesUnderlyingProperty);
        var hidesUnderlying = applyHidesUnderlying ? " new" : string.Empty;
        var declaringType = fieldType.WithNullableAnnotation(NullableAnnotation.None);
        var parameters = $"nameof({propertyName}),typeof({declaringType.ToDisplayString(CommonSymbolDisplayFormat.DefaultFormat)}),typeof({classSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}){customParameters}".Split(',');
        var propertyDeclaredAccessibility = fieldSymbol.GetValue<int>(attributeSymbol, AutoBindableConstants.AttrPropertyAccessibility);
        var propertyAccessibility =
                    propertyDeclaredAccessibility == 0
                        ? SyntaxFacts.GetText(classSymbol.DeclaredAccessibility)
                        : SyntaxFacts.GetText((Accessibility)propertyDeclaredAccessibility);

        w._(AttributeBuilder.GetAttrGeneratedCodeString());
        w._($@"{propertyAccessibility} static{hidesUnderlying} readonly {AutoBindableConstants.FullNameMauiControls}.BindableProperty {bindablePropertyName} =");
        w._($"{w.GetIndentString(6)}{AutoBindableConstants.FullNameMauiControls}.BindableProperty.Create(");

        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var ends = i < parameters.Length - 1 ? "," : ");";
            w._($"{w.GetIndentString(12)}{parameter}{ends}");
        }

        w._();
        AttributeBuilder.WriteAllAttrGeneratedCodeStrings(w);
        using (w.B(@$"{propertyAccessibility}{hidesUnderlying} {fieldType.ToDisplayString(CommonSymbolDisplayFormat.DefaultFormat)} {propertyName}"))
        {
            w._($@"get => ({fieldType.ToDisplayString(CommonSymbolDisplayFormat.DefaultFormat)})GetValue({bindablePropertyName});");
            if (this.ExistsBodySetter())
            {
                using (w.B(@$"set"))
                {
                    w._($@"SetValue({bindablePropertyName}, value);");
                    this.ProcessBodySetter(w);
                }
            }
            else
            {
                w._($@"set => SetValue({bindablePropertyName}, value);");
            }
        }

        this.ProcessImplementationLogic(w);
    }

    private void InitializeImplementations(IFieldSymbol fieldSymbol, ISymbol attributeSymbol, INamedTypeSymbol classSymbol)
    {
        this.Implementations.Clear();
        var args = new object[] { fieldSymbol, attributeSymbol, classSymbol };
        this.TypeImplementations.ForEach(t => {
            var ctor = t.GetConstructors().FirstOrDefault();
            var paramLength = ctor.GetParameters().Length;
            var paramsCtor = args.Take(paramLength).ToArray();
            var instantiatedType = Activator.CreateInstance(t, paramsCtor) as IImplementation;
            this.Implementations.Add(instantiatedType);
        });
    }

    private string ProcessBindableParameters()
    {
        var parameters = this.Implementations
                                .Select(i => i.ProcessBindableParameters())
                                .Where(x => !string.IsNullOrEmpty(x)).ToArray();
        
        return parameters.Length > 0 ? $@",{ string.Join(",", parameters) }" : string.Empty;
    }

    private void ProcessBodySetter(CodeWriter w)
    {
        this.Implementations
                .ForEach(i => i.ProcessBodySetter(w));
    }

    private void ProcessImplementationLogic(CodeWriter w)
    {
        this.Implementations
            .ForEach(i => i.ProcessImplementationLogic(w));
    }

    private bool ExistsBodySetter()
    {
        return this.Implementations.Any(i => i.SetterImplemented());
    }

    private string ChooseName(string fieldName, TypedConstant overridenNameOpt)
    {
        if (!overridenNameOpt.IsNull)
        {
            return overridenNameOpt.Value?.ToString();
        }

        fieldName = fieldName.TrimStart('_');
        if (fieldName.Length == 0)
            return string.Empty;

        if (fieldName.Length == 1)
            return fieldName.ToUpper();

        return fieldName.Substring(0, 1).ToUpper() + fieldName.Substring(1);
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        this.Initialize(
            context,
            SourceText.From(attributeText, Encoding.UTF8),
            (attributeSymbol, group, spc) => {
                var classNamedTypeSymbol = group.Key;
                try
                {
                    var classSource = this.ProcessClass(classNamedTypeSymbol, group.ToList(), attributeSymbol, spc);
                    if (string.IsNullOrEmpty(classSource))
                        return;

                    spc.AddSource($"{classNamedTypeSymbol.Name}.generated.cs", SourceText.From(classSource, Encoding.UTF8));
                }
                catch (Exception e)
                {
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            new DiagnosticDescriptor(
                                AutoBindableConstants.ExceptionMBPG001Id,
                                AutoBindableConstants.ExceptionTitle,
                                AutoBindableConstants.ExceptionMBPG001Message,
                                AutoBindableConstants.ProjectName,
                                DiagnosticSeverity.Error,
                                isEnabledByDefault: true
                            ),
                            classNamedTypeSymbol.Locations.FirstOrDefault(),
                            classNamedTypeSymbol.ToDisplayString(),
                            e.ToString()
                        )
                    );
                }
            }
        );
    }
}
