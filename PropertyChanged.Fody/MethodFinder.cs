﻿using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

public partial class ModuleWeaver
{


    void ProcessChildNode(TypeNode node, EventInvokerMethod eventInvoker)
    {
        var childEventInvoker = FindEventInvokerMethod(node.TypeDefinition);
        if (childEventInvoker == null)
        {
            if (node.TypeDefinition.BaseType.IsGenericInstance)
            {
                var methodReference = MakeGeneric(node.TypeDefinition.BaseType, eventInvoker.MethodReference);
                eventInvoker = new EventInvokerMethod
                                   {
                                       InvokerType = eventInvoker.InvokerType,
                                       MethodReference = methodReference,
                                       IsVisibleFromChildren = eventInvoker.IsVisibleFromChildren 
                                   };
            }
        }
        else
        {
            eventInvoker = childEventInvoker;
        }

        if (!eventInvoker.IsVisibleFromChildren)
        {
            var error = string.Format("Cannot use '{0}' in '{1}' since that method is not visible from the child class.", eventInvoker.MethodReference.FullName, node.TypeDefinition.FullName);
            throw new WeavingException(error);
        }
        node.EventInvoker = eventInvoker;

        foreach (var childNode in node.Nodes)
        {
            ProcessChildNode(childNode, eventInvoker);
        }
    }



    public EventInvokerMethod RecursiveFindEventInvoker(TypeDefinition typeDefinition)
    {
        var typeDefinitions = new Stack<TypeDefinition>();
        MethodDefinition methodDefinition;
        var currentTypeDefinition = typeDefinition;
        do
        {
            typeDefinitions.Push(currentTypeDefinition);
         
            if (FindEventInvokerMethodDefinition(currentTypeDefinition, out methodDefinition))
            {
                break;
            }
            var baseType = currentTypeDefinition.BaseType;

            if (baseType == null || baseType.FullName == "System.Object")
            {
                return null;
            }
            currentTypeDefinition = Resolve(baseType);
        } while (true);

        return new EventInvokerMethod
                   {
                       MethodReference = GetMethodReference(typeDefinitions, methodDefinition),
                       IsVisibleFromChildren = IsVisibleFromChildren(methodDefinition),
                       InvokerType = ClassifyInvokerMethod(methodDefinition),
                   };
    }

    static bool IsVisibleFromChildren(MethodDefinition methodDefinition)
    {
        return methodDefinition.IsFamilyOrAssembly || methodDefinition.IsFamily || methodDefinition.IsFamilyAndAssembly || methodDefinition.IsPublic;
    }


    EventInvokerMethod FindEventInvokerMethod(TypeDefinition type)
    {
        MethodDefinition methodDefinition;
        if (FindEventInvokerMethodDefinition(type, out methodDefinition))
        {
            var methodReference = ModuleDefinition.Import(methodDefinition);
            return new EventInvokerMethod
                       {
                           MethodReference = methodReference.GetGeneric(),
                           IsVisibleFromChildren = IsVisibleFromChildren(methodDefinition),
                           InvokerType = ClassifyInvokerMethod(methodDefinition),
                       };
        }
        return null;
    }

    bool FindEventInvokerMethodDefinition(TypeDefinition type, out MethodDefinition methodDefinition)
    {
        methodDefinition = type.Methods
            .Where(x => (x.IsFamily || x.IsFamilyAndAssembly || x.IsPublic || x.IsFamilyOrAssembly) && EventInvokerNames.Contains(x.Name))
            .OrderByDescending(definition => definition.Parameters.Count)
            .FirstOrDefault(x => IsBeforeAfterMethod(x) || IsSingleStringMethod(x) || IsPropertyChangedArgMethod(x) || IsSenderPropertyChangedArgMethod(x));
        if (methodDefinition == null)
        {
            methodDefinition = type.Methods
                .Where(x => EventInvokerNames.Contains(x.Name))
                .OrderByDescending(definition => definition.Parameters.Count)
                .FirstOrDefault(x => IsBeforeAfterMethod(x) || IsSingleStringMethod(x) || IsPropertyChangedArgMethod(x) || IsSenderPropertyChangedArgMethod(x));
        }
        return methodDefinition != null;
    }

    public static InvokerTypes ClassifyInvokerMethod(MethodDefinition method)
    {
        if (IsSenderPropertyChangedArgMethod(method))
        {
            return InvokerTypes.SenderPropertyChangedArg;
        }
        if (IsPropertyChangedArgMethod(method))
        {
            return InvokerTypes.PropertyChangedArg;
        }
        if (IsBeforeAfterMethod(method))
        {
            return InvokerTypes.BeforeAfter;
        }

        return InvokerTypes.String;
    }

    public static bool IsSenderPropertyChangedArgMethod(MethodDefinition method)
    {
        var parameters = method.Parameters;
        return parameters.Count == 2
               && parameters[0].ParameterType.FullName == "System.Object"
               && parameters[1].ParameterType.FullName == "System.ComponentModel.PropertyChangedEventArgs";
    }

    public static bool IsPropertyChangedArgMethod(MethodDefinition method)
    {
        var parameters = method.Parameters;
        return parameters.Count == 1
               && parameters[0].ParameterType.FullName == "System.ComponentModel.PropertyChangedEventArgs";
    }

    public static bool IsSingleStringMethod(MethodDefinition method)
    {
        var parameters = method.Parameters;
        return parameters.Count == 1
               && parameters[0].ParameterType.FullName == "System.String";
    }

    public static bool IsBeforeAfterMethod(MethodDefinition method)
    {
        var parameters = method.Parameters;
        return parameters.Count == 3
               && parameters[0].ParameterType.FullName == "System.String"
               && parameters[1].ParameterType.FullName == "System.Object"
               && parameters[2].ParameterType.FullName == "System.Object";
    }


    public void FindMethodsForNodes()
    {
        foreach (var notifyNode in NotifyNodes)
        {
            var eventInvoker = RecursiveFindEventInvoker(notifyNode.TypeDefinition);
            if (eventInvoker == null)
            {
                eventInvoker = AddOnPropertyChangedMethod(notifyNode.TypeDefinition);
                if (eventInvoker == null)
                {
                    var message = string.Format("\tCould not find EventInvoker method on type '{1}'. Possible names are '{0}'. It is possible you are inheriting from a base class and have not correctly set 'EventInvokerNames' or you are using a explicit PropertyChanged event and the event field is not visible to this instance. Please either correct 'EventInvokerNames' or implement your own EventInvoker on this class. No derived types will be processed. If you want to suppress this message place a [DoNotNotifyAttribute] on {1}.", string.Join(", ", EventInvokerNames.ToArray()), notifyNode.TypeDefinition.Name);
                    LogWarning(message);
                    continue;
                }
            }

            notifyNode.EventInvoker = eventInvoker;

            foreach (var childNode in notifyNode.Nodes)
            {
                ProcessChildNode(childNode, eventInvoker);
            }
        }
    }

}