using System;
using System.Collections.Generic;
using System.Text;

using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL
{
	public static class TypeExt
	{
		public static bool IsCustomStringInterpolator(this IType type)
		{
			return type.IsStringInterpolator() && !type.IsKnownType(KnownTypeCode.DefaultInterpolatedStringHandler);
		}

		public static bool IsStringInterpolator(this IType type)
		{
			if (type.IsByRefLike)
				type = type.UnwrapByRef();
			
			if (type.IsKnownType(KnownTypeCode.DefaultInterpolatedStringHandler))
				return true;

			if (type.IsKnownType(KnownTypeCode.IL2CPPInterpolatedStringHandler))
				return true;

			if (type.IsKnownType(KnownTypeCode.IDirectWriterInterpolatedStringHandler))
				return true;

			if (type.IsKnownType(KnownTypeCode.IL2CPPCodeWriterUnindentedInterpolatedStringHandler))
				return true;

			return false;
			if (type.Kind != TypeKind.Struct)
				return false;

		
			// Extremely slow.
			if (type.Name.Equals("CodeWriterInterpolatedStringHandler", StringComparison.Ordinal))
				return true;

			return false;
		}
	}
}
