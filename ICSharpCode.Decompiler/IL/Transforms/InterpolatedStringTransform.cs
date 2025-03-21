﻿// Copyright (c) 2021 Siegfried Pammer
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
using System.Diagnostics;
using System.Linq;

using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	public class InterpolatedStringTransform : IStatementTransform
	{
		void IStatementTransform.Run(Block block, int pos, StatementTransformContext context)
		{
			if (!context.Settings.StringInterpolation)
				return;
			int interpolationStart = pos;
			int interpolationEnd;
			bool isCustomInterpolator = false;
			int argIndex = -1;

			ILInstruction insertionPoint;
			// stloc v(newobj DefaultInterpolatedStringHandler..ctor(ldc.i4 literalLength, ldc.i4 formattedCount))
			if (block.Instructions[pos] is StLoc {
				Variable: ILVariable { Kind: VariableKind.Local } v,
				Value: NewObj { Arguments: { Count: >= 2 } } newObj
			} stloc
				&& v.Type.IsStringInterpolator()
				&& newObj.Method.DeclaringType.IsStringInterpolator()
				&& newObj.Arguments[0].MatchLdcI4(out _)
				&& newObj.Arguments[1].MatchLdcI4(out _))
			{
				// { call MethodName(ldloca v, ...) }
				do
				{
					pos++;
				}
				while (IsKnownCall(block, pos, v));
				interpolationEnd = pos;
				// ... call ToStringAndClear(ldloca v) ...

				isCustomInterpolator = v.Type.IsCustomStringInterpolator();
				if (!FindToStringAndClear(block, pos, interpolationStart, interpolationEnd, v, out insertionPoint, out argIndex))
				{
					if (!isCustomInterpolator)
					{
						throw new Exception($"Can't find call passing IL2CPP Interpolated string handler");
					}

					return;
				}
				
				if (!(v.StoreCount == 1 && v.AddressCount == interpolationEnd - interpolationStart && v.LoadCount == 0))
				{
					return;
				}
			}
			else
			{
				return;
			}
			context.Step($"Transform DefaultInterpolatedStringHandler {v.Name}", stloc);
			v.Kind = VariableKind.InitializerTarget;
			var replacement = new Block(BlockKind.InterpolatedString);
			for (int i = interpolationStart; i < interpolationEnd; i++)
			{
				replacement.Instructions.Add(block.Instructions[i]);
			}

			if (!isCustomInterpolator)
			{
				var callToStringAndClear = insertionPoint;
				insertionPoint.ReplaceWith(replacement);
				replacement.FinalInstruction = callToStringAndClear;
				block.Instructions.RemoveRange(interpolationStart, interpolationEnd - interpolationStart);
			}
			else
			{
				
				CallInstruction originalCall = (CallInstruction)(insertionPoint);

				CallInstruction newCall = null;

				bool IsMethodAppropriate(IMethod m)
				{
					if (m.IsStatic == originalCall.Method.IsStatic && m.Parameters.Count == originalCall.Method.Parameters.Count &&
						m.Name.Equals(originalCall.Method.Name, StringComparison.Ordinal))
					{
						if (m.Parameters[argIndex].Type.IsKnownType(KnownTypeCode.String))
							return true;
					}

					return false;
				}

				var replacementMethod = originalCall.Method.DeclaringType.GetMethods(IsMethodAppropriate, GetMemberOptions.IgnoreInheritedMembers).FirstOrDefault();
				if(replacementMethod == null)
				{
					replacementMethod = originalCall.Method.DeclaringType.GetMethods(IsMethodAppropriate, GetMemberOptions.None).First();
				}

				if(originalCall is CallVirt virt)
				{
					newCall = new CallVirt(replacementMethod);
				}
				else if(originalCall is Call c)
				{
					newCall = new Call(replacementMethod);
				}
				else
				{
					throw new NotImplementedException("This call type is not implemented for custom string interpolator");
				}

				foreach (var a in originalCall.Arguments)
					newCall.Arguments.Add(a);

				// account for this arg
				newCall.Arguments[argIndex + (newCall.Method.IsStatic ? 0 : 1)] = replacement;

				insertionPoint.ReplaceWith(newCall);
				replacement.FinalInstruction = new Nop();

				// Remove all the Append* instructions.
				block.Instructions.RemoveRange(interpolationStart, interpolationEnd - interpolationStart);
				
				// Insert interpolated string block
				//block.Instructions.Insert(interpolationStart, newCall);

				// Replace last instruction in the block with new interpolated string block.
				//insertionPoint.ReplaceWith(newCall);

				//block.Instructions.Add(call);
				
			}
			
		}

		private bool IsKnownCall(Block block, int pos, ILVariable v)
		{
			if (pos >= block.Instructions.Count - 1)
				return false;
			if (!(block.Instructions[pos] is Call call))
				return false;
			if (!(call.Arguments.Count > 1))
				return false;
			if (!call.Arguments[0].MatchLdLoca(v))
				return false;
			if (call.Method.IsStatic)
				return false;
			if (!call.Method.DeclaringType.IsStringInterpolator())
				return false;
			switch (call.Method.Name)
			{
				case "AppendLiteral" when call.Arguments.Count == 2 && call.Arguments[1] is LdStr:
				case "AppendFormatted" when call.Arguments.Count == 2:
				case "AppendFormatted" when call.Arguments.Count == 3 && call.Arguments[2] is LdStr:
				case "AppendFormatted" when call.Arguments.Count == 3 && call.Arguments[2] is LdcI4:
				case "AppendFormatted" when call.Arguments.Count == 4 && call.Arguments[2] is LdcI4 && call.Arguments[3] is LdStr:
					break;
				default:
					return false;
			}
			return true;
		}

		private bool FindToStringAndClear(Block block, int pos, int interpolationStart, int interpolationEnd, ILVariable v, out ILInstruction insertionPoint, out int argumentIndex)
		{
			insertionPoint = null;
			argumentIndex = -1;

			if (pos >= block.Instructions.Count)
				return false;
			// find
			// ... call ToStringAndClear(ldloca v) ...
			// in block.Instructions[pos]
			for (int i = interpolationStart; i < interpolationEnd; i++)
			{
				var result = ILInlining.FindLoadInNext(block.Instructions[pos], v, block.Instructions[i], InliningOptions.None);
				if (result.Type != ILInlining.FindResultType.Found)
					return false;
				insertionPoint ??= result.LoadInst.Parent;
				Debug.Assert(insertionPoint == result.LoadInst.Parent);
			}

			if (insertionPoint is not CallInstruction call)
				return false;

			if (call is { Arguments: { Count: 1 }, Method: { Name: "ToStringAndClear", IsStatic: false } })
				return true;

			if(call.Arguments is { Count: >= 1 } && call.Method is { Parameters: { Count: >= 1} })
			{
				var method = call.Method;
				insertionPoint = call;

				for (int i = 0; i < method.Parameters.Count; i++)
				{
					IParameter p = method.Parameters[i];
					if (p.Type.IsStringInterpolator())
					{
						argumentIndex = i;
						return true;
					}
				}
			}

			return false;
			/*
			return insertionPoint is Call {
				Arguments: { Count: 1 },
				Method: { Name: "ToStringAndClear", IsStatic: false }
			};
			*/
		}

		private bool FindArgumentPassing(Block block, int pos, int interpolationStart, int interpolationEnd, ILVariable v, out ILInstruction insertionPoint)
		{
			insertionPoint = null;
			if (pos >= block.Instructions.Count)
				return false;

			// find 
			// call SomeMethod(... (ref, in) StringInterpolationArgument ...)
			// in block.Instructions[pos]

			for (int i = interpolationStart; i < interpolationEnd; i++)
			{
				var result = ILInlining.FindLoadInNext(block.Instructions[pos], v, block.Instructions[i], InliningOptions.None);
				if (result.Type != ILInlining.FindResultType.Found)
					return false;
				insertionPoint ??= result.LoadInst.Parent;
				Debug.Assert(insertionPoint == result.LoadInst.Parent);
			}

			return insertionPoint is Call {
				Arguments: { Count: 1 },
				Method: { Name: "ToStringAndClear", IsStatic: false }
			};
		}
	}
}