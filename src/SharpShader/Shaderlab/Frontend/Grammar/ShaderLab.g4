grammar ShaderLab;

shaderFile
    : shaderDecl EOF
    ;

shaderDecl
    : SHADER STRING LBRACE shaderBody RBRACE
    ;

shaderBody
    : propertiesSection? shaderTagsSection? passSection*
    ;

propertiesSection
    : PROPERTIES genericBlock
    ;

shaderTagsSection
    : TAGS tagsBlock
    ;

passSection
    : PASS passBlock
    ;

passBlock
    : LBRACE passElement* RBRACE
    ;

passElement
    : passTagsSection
    | stencilSection
    | hlslProgramSection
    | stateStatement
    ;

passTagsSection
    : TAGS tagsBlock
    ;

stencilSection
    : STENCIL genericBlock
    ;

hlslProgramSection
    : HLSLPROGRAM HLSL_PLACEHOLDER ENDHLSL
    ;

stateStatement
    : stateToken+
    ;

stateToken
    : SHADER
    | PROPERTIES
    | TAGS
    | PASS
    | STENCIL
    | HLSLPROGRAM
    | ENDHLSL
    | CATEGORY
    | IDENTIFIER
    | STRING
    | NUMBER
    | LPAREN
    | RPAREN
    | LBRACKET
    | RBRACKET
    | COMMA
    | EQUAL
    | DOT
    | COLON
    | PLUS
    | MINUS
    ;

tagsBlock
    : LBRACE tagPair* RBRACE
    ;

tagPair
    : STRING EQUAL STRING
    ;

genericBlock
    : LBRACE genericBlockElement* RBRACE
    ;

genericBlockElement
    : genericBlock
    | stateToken
    ;

SHADER: S H A D E R;
PROPERTIES: P R O P E R T I E S;
TAGS: T A G S;
PASS: P A S S;
STENCIL: S T E N C I L;
HLSLPROGRAM: H L S L P R O G R A M;
ENDHLSL: E N D H L S L;
CATEGORY: C A T E G O R Y;

HLSL_PLACEHOLDER: '__HLSL_BLOCK_' [0-9]+ '__';

IDENTIFIER: [A-Za-z_][A-Za-z0-9_]*;
NUMBER: [0-9]+ ('.' [0-9]*)? ([eE] [+-]? [0-9]+)? [fF]?;
STRING: '"' ( '\\' . | ~["\\] )* '"';

LBRACE: '{';
RBRACE: '}';
LPAREN: '(';
RPAREN: ')';
LBRACKET: '[';
RBRACKET: ']';
COMMA: ',';
EQUAL: '=';
DOT: '.';
COLON: ':';
PLUS: '+';
MINUS: '-';

LINE_COMMENT: '//' ~[\r\n]* -> skip;
BLOCK_COMMENT: '/*' .*? '*/' -> skip;
WS: [ \t\r\n]+ -> skip;

fragment A: [aA];
fragment B: [bB];
fragment C: [cC];
fragment D: [dD];
fragment E: [eE];
fragment F: [fF];
fragment G: [gG];
fragment H: [hH];
fragment I: [iI];
fragment J: [jJ];
fragment K: [kK];
fragment L: [lL];
fragment M: [mM];
fragment N: [nN];
fragment O: [oO];
fragment P: [pP];
fragment Q: [qQ];
fragment R: [rR];
fragment S: [sS];
fragment T: [tT];
fragment U: [uU];
fragment V: [vV];
fragment W: [wW];
fragment X: [xX];
fragment Y: [yY];
fragment Z: [zZ];
