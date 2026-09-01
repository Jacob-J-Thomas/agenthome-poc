using System.Reflection;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

public sealed class HumanReviewPublicApiBoundaryTests
{
    private const string ApplicationAssemblyName = "EmbodySense.Core.Application";
    private const string CommonAssemblyName = "EmbodySense.Core.Common";
    private const string HumanReviewNamespace = "EmbodySense.Core.Startup.HumanReview";
    private const string RecoveryNamespace = "EmbodySense.Core.Startup.Loops.Execution.Sleep";

    [Fact]
    public void HumanReviewStartupSurface_does_not_expose_application_or_common_types()
    {
        var violations = FindViolations(GetSurfaceTypes(HumanReviewNamespace, static _ => true), inspectInheritedMembers: true);

        Assert.True(violations.Count == 0, FormatViolations(violations));
    }

    [Fact]
    public void HumanReviewRecoverySurface_does_not_expose_application_or_common_types()
    {
        var violations = FindViolations(GetSurfaceTypes(RecoveryNamespace, static type =>
            type == typeof(IHumanReviewRecoveryRunner)
            || type.Name.StartsWith("HumanReview", StringComparison.Ordinal)), inspectInheritedMembers: false);

        Assert.True(violations.Count == 0, FormatViolations(violations));
    }

    private static IReadOnlyList<Type> GetSurfaceTypes(string namespaceName, Func<Type, bool> predicate)
        => typeof(HumanReviewRuntimeFacade).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace is not null
                && (string.Equals(type.Namespace, namespaceName, StringComparison.Ordinal)
                    || type.Namespace.StartsWith(namespaceName + ".", StringComparison.Ordinal))
                && predicate(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> FindViolations(IEnumerable<Type> surfaceTypes, bool inspectInheritedMembers)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in surfaceTypes)
        {
            InspectType(type.BaseType, $"{type.FullName} base type", violations);
            foreach (var interfaceType in type.GetInterfaces())
            {
                InspectType(interfaceType, $"{type.FullName} interface", violations);
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    InspectParameter(parameter, $"{type.FullName}.{constructor.Name} parameter '{parameter.Name}'", violations);
                }
            }

            var memberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!inspectInheritedMembers)
            {
                memberFlags |= BindingFlags.DeclaredOnly;
            }

            foreach (var method in type.GetMethods(memberFlags))
            {
                if (!inspectInheritedMembers && IsExistingLocalWorkContract(type, method))
                {
                    continue;
                }

                InspectType(method.ReturnType, $"{type.FullName}.{method.Name} return type", violations);
                InspectCustomModifiers(method.ReturnParameter, $"{type.FullName}.{method.Name} return modifiers", violations);
                foreach (var parameter in method.GetParameters())
                {
                    InspectParameter(parameter, $"{type.FullName}.{method.Name} parameter '{parameter.Name}'", violations);
                }

                foreach (var genericParameter in method.GetGenericArguments())
                {
                    InspectGenericParameter(genericParameter, $"{type.FullName}.{method.Name} generic parameter '{genericParameter.Name}'", violations);
                }
            }

            foreach (var property in type.GetProperties(memberFlags))
            {
                InspectType(property.PropertyType, $"{type.FullName}.{property.Name} property type", violations);
                InspectCustomModifiers(property, $"{type.FullName}.{property.Name} property modifiers", violations);
                foreach (var parameter in property.GetIndexParameters())
                {
                    InspectParameter(parameter, $"{type.FullName}.{property.Name} index parameter '{parameter.Name}'", violations);
                }
            }

            foreach (var field in type.GetFields(memberFlags))
            {
                InspectType(field.FieldType, $"{type.FullName}.{field.Name} field type", violations);
                InspectCustomModifiers(field, $"{type.FullName}.{field.Name} field modifiers", violations);
            }

            foreach (var @event in type.GetEvents(memberFlags))
            {
                InspectType(@event.EventHandlerType, $"{type.FullName}.{@event.Name} event type", violations);
            }

            foreach (var genericParameter in type.GetGenericArguments())
            {
                InspectGenericParameter(genericParameter, $"{type.FullName} generic parameter '{genericParameter.Name}'", violations);
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static void InspectParameter(ParameterInfo parameter, string location, ICollection<string> violations)
    {
        InspectType(parameter.ParameterType, location, violations);
        InspectCustomModifiers(parameter, $"{location} modifiers", violations);
    }

    private static bool IsExistingLocalWorkContract(Type surfaceType, MethodInfo method)
        => surfaceType == typeof(HumanReviewRecoveryRunner)
            && method.Name is nameof(IGovernedLoopLocalWorkRunner.RunOnceAsync) or nameof(IGovernedLoopLocalWorkReadinessProbe.ProbeReadinessAsync);

    private static void InspectGenericParameter(Type genericParameter, string location, ICollection<string> violations)
    {
        foreach (var constraint in genericParameter.GetGenericParameterConstraints())
        {
            InspectType(constraint, $"{location} constraint", violations);
        }
    }

    private static void InspectCustomModifiers(MemberInfo member, string location, ICollection<string> violations)
    {
        switch (member)
        {
            case FieldInfo field:
                InspectTypes(field.GetRequiredCustomModifiers(), $"{location} required", violations);
                InspectTypes(field.GetOptionalCustomModifiers(), $"{location} optional", violations);
                break;
            case PropertyInfo property:
                InspectTypes(property.GetRequiredCustomModifiers(), $"{location} required", violations);
                InspectTypes(property.GetOptionalCustomModifiers(), $"{location} optional", violations);
                break;
        }
    }

    private static void InspectCustomModifiers(ParameterInfo parameter, string location, ICollection<string> violations)
    {
        InspectTypes(parameter.GetRequiredCustomModifiers(), $"{location} required", violations);
        InspectTypes(parameter.GetOptionalCustomModifiers(), $"{location} optional", violations);
    }

    private static void InspectTypes(IEnumerable<Type> types, string location, ICollection<string> violations)
    {
        foreach (var type in types)
        {
            InspectType(type, location, violations);
        }
    }

    private static void InspectType(Type? type, string location, ICollection<string> violations)
    {
        if (type is null)
        {
            return;
        }

        var assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is ApplicationAssemblyName or CommonAssemblyName)
        {
            violations.Add($"{location} exposes {type.FullName ?? type.Name} from {assemblyName}");
        }

        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            InspectType(type.GetElementType(), $"{location} element", violations);
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                InspectType(argument, $"{location} generic argument", violations);
            }
        }

        if (type.IsGenericParameter)
        {
            InspectGenericParameter(type, $"{location} generic parameter '{type.Name}'", violations);
        }
    }

    private static string FormatViolations(IReadOnlyList<string> violations)
        => violations.Count == 0
            ? string.Empty
            : $"Public Human Review API boundary violations ({violations.Count}):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}";
}
