using System;
using System.Collections.Generic;

namespace DarkBasicYo.Ast
{

    public class TypeReferenceNode : AstNode
    {
        private static readonly Dictionary<LexemType, VariableType> _map = new Dictionary<LexemType, VariableType>
        {
            [LexemType.KeywordTypeBoolean] = VariableType.Boolean,
            [LexemType.KeywordTypeByte] = VariableType.Byte,
            [LexemType.KeywordTypeFloat] = VariableType.Float,
            [LexemType.KeywordTypeInteger] = VariableType.Integer,
            [LexemType.KeywordTypeWord] = VariableType.Word,
            [LexemType.KeywordTypeDWord] = VariableType.DWord,
            [LexemType.KeywordTypeString] = VariableType.String,
            [LexemType.KeywordTypeDoubleFloat] = VariableType.DoubleFloat,
            [LexemType.KeywordTypeDoubleInteger] = VariableType.DoubleInteger,
        };

        public VariableType variableType;

        public TypeReferenceNode(Token token) : base(token)
        {
            variableType = Convert(token.type);
        }

        public static VariableType Convert(LexemType type)
        {
            if (!_map.TryGetValue(type, out var variableType))
            {
                throw new NotImplementedException("Custom types are not supported yet :(");
            }

            return variableType;
        }

        protected override string GetString()
        {
            return variableType.ToString().ToLowerInvariant();
        }
    }
}