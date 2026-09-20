using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UELib.Branch;
using UELib.ObjectModel.Annotations;
using UELib.Tokens;

namespace UELib.Core
{
    public partial class UStruct
    {
        public partial class UByteCodeDecompiler
        {
            [ExprToken(ExprToken.EndFunctionParms)]
            public class EndFunctionParmsToken : Token
            {
            }

            public abstract class FunctionToken : Token
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                protected UName DeserializeFunctionName(IUnrealStream stream)
                {
                    return ReadName(stream);
                }

                protected virtual void DeserializeCall(IUnrealStream stream)
                {
                    DeserializeParms();
                    Decompiler.DeserializeDebugToken();
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private void DeserializeParms()
                {
#pragma warning disable 642
                    while (!(DeserializeNext() is EndFunctionParmsToken)) ;
#pragma warning restore 642
                }

                private static byte GetInfixOperPrecedence(Token t)
                {
                    switch (t)
                    {
                        case NativeFunctionToken token when token.NativeItem.Type == FunctionType.Operator:
                            return token.NativeItem.OperPrecedence;

                        case FinalFunctionToken token when token.Function.IsOperator()
                                                            && !token.Function.IsPre()
                                                            && !token.Function.IsPost():
                            return token.Function.OperPrecedence;

                        default:
                            return 0;
                    }
                }

                /// <summary>
                /// Tokens that decompile transparently to the expression that follows them, and may therefore sit
                /// between an operator and one of its operands. The most important one is the <see cref="SkipToken"/>
                /// that precedes the "skip" parameter of the short-circuiting operators (&amp;&amp; and ||).
                /// </summary>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private static bool IsTransparentOperandWrapper(Token t)
                {
                    return t is SkipToken
                           || t is ResizeStringToken
                           || t is LineNumberToken;
                }

                /// <summary>
                /// Resolves the token that actually forms an operand's expression by looking through any transparent
                /// wrapper tokens, so that the operand's operator precedence can be inspected.
                ///
                /// This does not advance the decompiler; the wrapper token itself must still be the one that gets decompiled.
                /// </summary>
                private Token ResolveOperandToken(Token t)
                {
                    var tokens = Decompiler.DeserializedTokens;
                    int index = tokens.IndexOf(t);
                    if (index == -1)
                        return t;

                    while (IsTransparentOperandWrapper(t))
                    {
                        do
                        {
                            if (++index >= tokens.Count)
                                return t;
                        } while (tokens[index] is DebugInfoToken);

                        t = tokens[index];
                    }

                    return t;
                }

                /// <param name="isRightOperand">
                /// UnrealScript binds operators of equal precedence from left to right: the compiler only nests an
                /// operator into a right-hand operand when its precedence is strictly lower (see
                /// <c>FScriptCompiler::CompileExpr</c>). A right-hand operand of equal precedence can therefore only
                /// have come from explicit parentheses in the source, and must be parenthesized again to preserve the
                /// expression tree; a left-hand operand of equal precedence must not be.
                /// </param>
                private string PrecedenceToken(Token t, byte parentPrecedence, bool isRightOperand = false)
                {
                    var operand = ResolveOperandToken(t);
                    if (!(operand is FunctionToken))
                        return t.Decompile();

                    byte childPrecedence = GetInfixOperPrecedence(operand);
                    if (childPrecedence == 0)
                        return t.Decompile();

                    bool needsParentheses = isRightOperand
                        ? childPrecedence >= parentPrecedence
                        : childPrecedence > parentPrecedence;

                    return needsParentheses
                        ? $"({t.Decompile()})"
                        : t.Decompile();
                }

                /// <summary>
                /// A unary operator binds tighter than any infix operator, so an infix operand must be parenthesized
                /// (e.g. <c>!(A || B)</c>). A function call is self-delimiting and never needs them (<c>!IsA('Foo')</c>).
                ///
                /// A nested pre-operator only needs them when writing the two operators back to back would lex as a
                /// different token: <c>-(-A)</c> must not become <c>--A</c> (a pre-decrement).
                /// </summary>
                private static bool UnaryOperandNeedsParentheses(Token t, string operatorName)
                {
                    return t switch
                    {
                        NativeFunctionToken { NativeItem.Type: FunctionType.Operator } => true,
                        NativeFunctionToken { NativeItem.Type: FunctionType.PreOperator } inner
                            when operatorName == "-" && inner.NativeItem.Name == operatorName => true,
                        FinalFunctionToken { Function: var function } when function.IsOperator() => true,
                        _ => GetInfixOperPrecedence(t) > 0
                    };
                }

                private string DecompileUnaryOperand(Token t, string operatorName)
                {
                    return UnaryOperandNeedsParentheses(t, operatorName)
                        ? $"({t.Decompile()})"
                        : PrecedenceToken(t, 0);
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private bool NeedsSpace(string operatorName)
                {
                    return char.IsUpper(operatorName[0])
                           || char.IsLower(operatorName[0]);
                }

                protected string DecompilePreOperator(string operatorName)
                {
                    var operandToken = NextToken();
                    string operand = DecompileUnaryOperand(operandToken, operatorName);
                    AssertSkipCurrentToken<EndFunctionParmsToken>();

                    // Only space out if we have a non-symbol operator name.
                    return NeedsSpace(operatorName)
                        ? $"{operatorName} {operand}"
                        : $"{operatorName}{operand}";
                }

                protected string DecompileOperator(string operatorName, byte operPrecedence = byte.MaxValue)
                {
                    string leftOperand = PrecedenceToken(NextToken(), operPrecedence);
                    string rightOperand = PrecedenceToken(NextToken(), operPrecedence, isRightOperand: true);
                    AssertSkipCurrentToken<EndFunctionParmsToken>();
                    return $"{leftOperand} {operatorName} {rightOperand}";
                }

                protected string DecompilePostOperator(string operatorName)
                {
                    var operandToken = NextToken();
                    string operand = DecompileUnaryOperand(operandToken, operatorName);
                    AssertSkipCurrentToken<EndFunctionParmsToken>();

                    // Only space out if we have a non-symbol operator name.
                    return NeedsSpace(operatorName)
                        ? $"{operand} {operatorName}"
                        : $"{operand}{operatorName}";
                }

                protected string DecompileCall(string functionName)
                {
                    if (Decompiler._IsWithinClassContext)
                    {
                        functionName = $"static.{functionName}";

                        // Set false elsewhere as well but to be sure we set it to false here to avoid getting static calls inside the params.
                        // e.g.
                        // A1233343.DrawText(Class'BTClient_Interaction'.static.A1233332(static.Max(0, A1233328 - A1233322[A1233222].StartTime)), true);
                        Decompiler._IsWithinClassContext = false;
                    }

                    string arguments = DecompileParms();
                    var output = $"{functionName}({arguments})";
                    return output;
                }

                private string DecompileParms()
                {
                    var tokens = new List<Tuple<Token, string>>();
                    {
                    next:
                        var t = NextToken();
                        tokens.Add(Tuple.Create(t, t.Decompile()));
                        if (!(t is EndFunctionParmsToken))
                            goto next;
                    }

                    var output = new StringBuilder();
                    for (var i = 0; i < tokens.Count; ++i)
                    {
                        var t = tokens[i].Item1; // Token
                        string v = tokens[i].Item2; // Value

                        switch (t)
                        {
                            // Skipped optional parameters
                            case EmptyParmToken _:
                                output.Append(v);
                                break;

                            // End ")"
                            case EndFunctionParmsToken _:
                                output = new StringBuilder(output.ToString().TrimEnd(','));
                                break;

                            // Any passed values
                            default:
                                {
                                    if (i != tokens.Count - 1 && i > 0) // Skipped optional parameters
                                    {
                                        output.Append(v == string.Empty ? "," : ", ");
                                    }

                                    output.Append(v);
                                    break;
                                }
                        }
                    }

                    return output.ToString();
                }
            }

            [ExprToken(ExprToken.FinalFunction)]
            public class FinalFunctionToken : FunctionToken
            {
                public UFunction Function;

                public override void Deserialize(IUnrealStream stream)
                {
                    Function = stream.ReadObject<UFunction>();
                    Decompiler.AlignObjectSize();

                    DeserializeCall(stream);
                }

                public override string Decompile()
                {
                    var output = string.Empty;
                    // Support for non native operators.
                    if (Function.IsPost())
                    {
                        output = DecompilePostOperator(Function.FriendlyName);
                    }
                    else if (Function.IsPre())
                    {
                        output = DecompilePreOperator(Function.FriendlyName);
                    }
                    else if (Function.IsOperator())
                    {
                        output = DecompileOperator(Function.FriendlyName, Function.OperPrecedence);
                    }
                    else
                    {
                        // Calling Super??.
                        if (Function.Name == Decompiler._Container.Name && !Decompiler._IsWithinClassContext)
                        {
                            output = "super";

                            // Check if the super call is within the super class of this functions outer(class)
                            var container = Decompiler._Container;
                            var context = (UField)container.Outer;
                            // ReSharper disable once PossibleNullReferenceException
                            var contextFuncOuterName = context.Name;
                            // ReSharper disable once PossibleNullReferenceException
                            var callFuncOuterName = Function.Outer.Name;
                            if (context.Super == null || callFuncOuterName != context.Super.Name)
                            {
                                // If there's no super to call, then we have a recursive call.
                                if (container.Super == null)
                                {
                                    output += $"({contextFuncOuterName})";
                                }
                                else
                                {
                                    // Different owners, then it is a deep super call.
                                    if (callFuncOuterName != contextFuncOuterName)
                                    {
                                        output += $"({callFuncOuterName})";
                                    }
                                }
                            }

                            output += ".";
                        }

                        output += DecompileCall(Function.Name);
                    }

                    Decompiler._CanAddSemicolon = true;
                    return output;
                }
            }

            [ExprToken(ExprToken.VirtualFunction)]
            public class VirtualFunctionToken : FunctionToken
            {
                public UName FunctionName;

                public override void Deserialize(IUnrealStream stream)
                {
                    // FIXME: Version, seen in EndWar (222) and R6Vegas (v241), gone at least since RoboBlitz (369)
                    if (stream.Version >= (uint)PackageObjectLegacyVersion.UE3 &&
                        stream.Version <= 241)
                    {
                        byte isSuper = stream.ReadByte();
                        Decompiler.AlignSize(sizeof(byte));
                    }

                    FunctionName = DeserializeFunctionName(stream);
                    DeserializeCall(stream);
                }

                public override string Decompile()
                {
                    Decompiler._CanAddSemicolon = true;
                    return DecompileCall(FunctionName);
                }
            }

            [ExprToken(ExprToken.GlobalFunction)]
            public class GlobalFunctionToken : FunctionToken
            {
                public UName FunctionName;

                public override void Deserialize(IUnrealStream stream)
                {
                    FunctionName = DeserializeFunctionName(stream);
                    DeserializeCall(stream);
                }

                public override string Decompile()
                {
                    Decompiler._CanAddSemicolon = true;
                    return $"global.{DecompileCall(FunctionName)}";
                }
            }

            [ExprToken(ExprToken.DelegateFunction)]
            public class DelegateFunctionToken : FunctionToken
            {
                public byte? IsLocal;
                public UProperty DelegateProperty;
                public UName FunctionName;

                public override void Deserialize(IUnrealStream stream)
                {
                    // FIXME: Version
                    if (stream.Version >= (uint)PackageObjectLegacyVersion.IsLocalAddedToDelegateFunctionToken)
                    {
                        IsLocal = stream.ReadByte();
                        Decompiler.AlignSize(sizeof(byte));
                    }

                    DelegateProperty = stream.ReadObject<UProperty>();
                    Decompiler.AlignObjectSize();

                    FunctionName = DeserializeFunctionName(stream);
                    DeserializeCall(stream);
                }

                public override string Decompile()
                {
                    Decompiler._CanAddSemicolon = true;
                    return DecompileCall(FunctionName);
                }
            }

            [ExprToken(ExprToken.NativeFunction)]
            public class NativeFunctionToken : FunctionToken
            {
                public NativeTableItem NativeItem;

                public override void Deserialize(IUnrealStream stream)
                {
                    DeserializeCall(stream);
                }

                public override string Decompile()
                {
                    string output;
                    switch (NativeItem.Type)
                    {
                        case FunctionType.Function:
                            output = DecompileCall(NativeItem.Name);
                            break;

                        case FunctionType.Operator:
                            output = DecompileOperator(NativeItem.Name, NativeItem.OperPrecedence);
                            break;

                        case FunctionType.PostOperator:
                            output = DecompilePostOperator(NativeItem.Name);
                            break;

                        case FunctionType.PreOperator:
                            output = DecompilePreOperator(NativeItem.Name);
                            break;

                        default:
                            output = DecompileCall(NativeItem.Name);
                            break;
                    }

                    Decompiler._CanAddSemicolon = true;
                    return output;
                }
            }
        }
    }
}
