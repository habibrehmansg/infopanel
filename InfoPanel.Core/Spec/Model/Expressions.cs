using System.Collections.Generic;

namespace InfoPanel.Spec;

/// <summary>
/// An expression of the scene format (<c>urn:infopanel:expression:1</c>, docs/expressions.md): a literal (number, string or
/// bool; a string is a colour where a colour is expected) or an operation. Read and written by <see cref="SpecJson"/>.
/// </summary>
public abstract record Expr;

/// <summary>A JSON number.</summary>
public sealed record NumberLit(double Value) : Expr;

/// <summary>A JSON string: text, or a colour literal (#RRGGBB, #RRGGBBAA) where a colour is expected.</summary>
public sealed record StringLit(string Value) : Expr;

/// <summary>A JSON true or false.</summary>
public sealed record BoolLit(bool Value) : Expr;

/// <summary>
/// The operations whose operands are positional (<c>args</c>): arithmetic, comparisons, logic, <c>concat</c>, <c>upper</c>,
/// <c>lower</c>, <c>rgba</c>, <c>alpha</c>, <c>lerp</c> and <c>if</c>.
/// </summary>
public sealed record CallExpr(string Op, IReadOnlyList<Expr> Args) : Expr;

/// <summary><c>{"op":"input"}</c>: a reading of a scene input; <see cref="Field"/> null means <c>value</c>.</summary>
public sealed record InputExpr(string Name, string? Field) : Expr;

/// <summary>
/// <c>{"op":"history"}</c>: an aggregate over the last <see cref="Count"/> samples of a number input's history, or with
/// <see cref="Column"/> of a table input's column; <see cref="P"/> only with <c>percentile</c>, <see cref="Lo"/> and
/// <see cref="Hi"/> only with <c>share</c>.
/// </summary>
public sealed record HistoryExpr(string Input, string Fn, int Count, ColumnRef? Column = null, Expr? P = null, Expr? Lo = null, Expr? Hi = null) : Expr;

/// <summary><c>{"op":"time"}</c>: a number from the tick's clock, or from the instant <see cref="At"/> when given.</summary>
public sealed record TimeExpr(string Field, string? Zone, Expr? At = null) : Expr;

/// <summary><c>{"op":"formatTime"}</c>: the tick's date and time, or the instant <see cref="At"/>'s, as text.</summary>
public sealed record FormatTimeExpr(string Pattern, string? Locale, string? Zone, Expr? At = null) : Expr;

/// <summary><c>{"op":"format"}</c>: a number as text.</summary>
public sealed record FormatExpr(Expr Value, Expr? Decimals, Expr? Group, string? DecimalSeparator, string? GroupSeparator) : Expr;

/// <summary><c>{"op":"box"}</c>: the item's layout box width or height, or its rotation on the canvas.</summary>
public sealed record BoxExpr(string Field) : Expr;

/// <summary><c>{"op":"measure"}</c>: a text node's measured width or height.</summary>
public sealed record MeasureExpr(string Node, string Field) : Expr;

/// <summary><c>{"op":"dt"}</c>: seconds since the previous state update.</summary>
public sealed record DtExpr : Expr;

/// <summary><c>{"op":"value"}</c>: a named value of the scene.</summary>
public sealed record ValueRefExpr(string Name) : Expr;

/// <summary><c>{"op":"state"}</c>: a state cell of the scene.</summary>
public sealed record StateRefExpr(string Name) : Expr;

/// <summary><c>{"op":"parse"}</c>: <see cref="Value"/>'s text read as a number or a colour (<see cref="As"/>), else <see cref="Else"/>.</summary>
public sealed record ParseExpr(string As, Expr Value, Expr Else) : Expr;

/// <summary>
/// <c>{"op":"cell"}</c>: one cell of a table input's reading, its text or (<see cref="Field"/> <c>number</c>) its number;
/// the column by <see cref="Index"/> or by <see cref="Name"/>, one of them set; <see cref="Field"/> null means <c>text</c>.
/// </summary>
public sealed record CellExpr(string Input, Expr Row, int? Index, string? Name, string? Field) : Expr;

/// <summary><c>{"op":"steps"}</c>: <c>Out[i]</c>, where i is how many of <see cref="At"/> the value is at or above.</summary>
public sealed record StepsExpr(Expr Value, IReadOnlyList<Expr> At, IReadOnlyList<Expr> Out) : Expr;
