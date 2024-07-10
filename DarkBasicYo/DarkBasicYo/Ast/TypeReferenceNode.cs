using System;
using System.Collections.Generic;

namespace DarkBasicYo.Ast
{

    public interface ITypeReferenceNode : IAstNode, IAstVisitable
    {
        VariableType variableType { get; }
    }
    
    public class StructTypeReferenceNode : AstNode, ITypeReferenceNode
    {
        public VariableRefNode variableNode;
        
        public StructTypeReferenceNode(VariableRefNode variableRefNode)
        {
            variableNode = variableRefNode;
            startToken = variableNode.startToken;
            endToken = variableRefNode.endToken;
        }
        
        protected override string GetString()
        {
            return $"typeRef {variableNode.variableName}";
        }

        public VariableType variableType => VariableType.Struct;
        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return variableNode;
        }
    }
    
    public class TypeReferenceNode : AstNode, ITypeReferenceNode
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
            [LexemType.VariableGeneral] = VariableType.Integer,
            [LexemType.VariableReal] = VariableType.Float,
            [LexemType.VariableString] = VariableType.String,
            
        };

        public VariableType variableType { get; private set; }

        public TypeReferenceNode(Token token) : base(token)
        {
            variableType = Convert(token.type);
        }

        public TypeReferenceNode(VariableType type, Token token) : base(token)
        {
            variableType = type;
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

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }
    }
}