#region License
/* ****************************************************************************
 * Copyright (c) Llewellyn Pritchard. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. 
 * A copy of the license can be found in the License.html file at the root of this distribution. 
 * By using this source code in any fashion, you are agreeing to be bound by the terms of the 
 * Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 * ***************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Scripting.Ast;
using IronScheme.Runtime;
using Microsoft.Scripting;
using IronScheme.Hosting;
using Microsoft.Scripting.Actions;
using System.Reflection;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Utils;
using System.Diagnostics;
using Microsoft.Scripting.Math;

[assembly: Extension(GeneratorType=typeof(Generator), BuiltinsType=typeof(Builtins))]

namespace IronScheme.Compiler
{

  public partial class Generator
  {
    static Generator()
    {
      Initialize();
    }

    [ThreadStatic]
    static SourceSpan spanhint;

    protected static SourceSpan SpanHint
    {
      get { return Generator.spanhint; }
      set { Generator.spanhint = value; }
    }

    // this is probably not very threadsafe....
    protected static Dictionary<SymbolId, CodeBlockExpression> references = new Dictionary<SymbolId, CodeBlockExpression>();

    protected internal readonly static FieldInfo Unspecified = typeof(Builtins).GetField("Unspecified");
    protected internal readonly static FieldInfo True = typeof(RuntimeHelpers).GetField("True");
    protected internal readonly static FieldInfo False = typeof(RuntimeHelpers).GetField("False");
    internal static bool inconstant = false;

    protected static Expression GetCons(object args, CodeBlock cb)
    {
      Cons c = args as Cons;
      if (c != null)
      {
        if (inconstant)
        {
          return GetConsList(c, cb);
        }
        else
        {
          return Ast.Constant(new IronSchemeConstant(c, cb));
        }
      }
      object[] v = args as object[];
      if (v != null)
      {
        return GetConsVector(v, cb);
      }
      else if (args is byte[])
      {
        Expression[] ba = Array.ConvertAll<byte, Expression>(args as byte[], delegate(byte b) { return Ast.Constant(b); });
        return Ast.NewArray(typeof(byte[]), ba);
      }
      else if (args is Fraction)
      {
        Fraction f = (Fraction) args;
        return Ast.Constant(new FractionConstant(f));
      }
      else
      {
        if (args is long)
        {
          args = (BigInteger)(long)args;
        }
        if (args != null && args.GetType().Name == "stx")
        {
          args = new SerializedConstant(args);
        }
        return Ast.Constant(args);
      }
    }

    static bool IsSimpleCons(Cons c)
    {
      if (c == null)
      {
        return true;
      }
      return !(c.car is Cons) && (c.cdr == null || IsSimpleCons(c.cdr as Cons));
    }

    protected readonly static Dictionary<SymbolId, bool> assigns = new Dictionary<SymbolId, bool>();

    protected internal static Expression GetAst(object args, CodeBlock cb)
    {
      return GetAst(args, cb, false);
    }

    static bool IsSimpleExpression(Expression e)
    {
      if (e is MethodCallExpression)
      {
        return IsSimpleCall((MethodCallExpression)e);
      }

      if (e is UnaryExpression)
      {
        UnaryExpression ue = (UnaryExpression)e;
        if (ue.NodeType == AstNodeType.Convert)
        {
          return IsSimpleExpression(ue.Operand);
        }
        return false;
      }

      if (e is BinaryExpression)
      {
        return IsSimpleExpression(((BinaryExpression)e).Left) &&
          IsSimpleExpression(((BinaryExpression)e).Right);
      }

      if (e is TypeBinaryExpression)
      {
        return IsSimpleExpression(((TypeBinaryExpression)e).Expression);
      }

      if (e is ConstantExpression)
      {
        ConstantExpression ce = (ConstantExpression)e;
        return Builtins.IsTrue(ce.Value);
      }

      if (e is BoundExpression)
      {
        return true;
      }
      else
      {
        return false;
      }
    }

    static bool IsSimpleCall(MethodCallExpression mce)
    {
      if (mce.Instance != null && !IsSimpleExpression(mce.Instance))
      {
        return false;
      }

      foreach (var arg in mce.Arguments)
      {
        if (arg is MethodCallExpression)
        {
          MethodCallExpression imce = (MethodCallExpression)arg;

          if (!IsSimpleCall(imce))
          {
            return false;
          }
        }

        if (!IsSimpleExpression(arg))
        {
          return false;
        }

      }

      return true;
    }

    static CodeBlock RewriteBody(CodeBlock cb)
    {
      CodeBlock ncb = Ast.CodeBlock("temp-inline:" + cb.Name);
      ncb.Parent = cb.Parent;

      foreach (var item in cb.Parameters)
      {
        Variable newvar = ncb.CreateParameter(item.Name, typeof(object));
        newvar.Lift = item.Lift;
      }

      foreach (var item in cb.Variables)
      {
        Variable newvar = ncb.CreateLocalVariable(item.Name, typeof(object));
        newvar.Lift = item.Lift;
      }

      Expression body = ((ReturnStatement)cb.Body).Expression;

      body = RewriteExpression(ncb, body);

      ncb.Body = Ast.Return(body);

      return ncb;
    }

    static Expression RewriteExpression(CodeBlock cb, Expression e)
    {
      if (e is MethodCallExpression)
      {
        MethodCallExpression mce = (MethodCallExpression)e;
        List<Expression> args = new List<Expression>();
        foreach (var arg in mce.Arguments)
        {
          args.Add(RewriteExpression(cb, arg));
        }

        return Ast.Call(RewriteExpression(cb, mce.Instance), mce.Method, args.ToArray());
      }
      if (e is BoundExpression)
      {
        BoundExpression be = (BoundExpression)e;
        return Ast.Read(cb.Lookup(be.Variable.Name));
      }

      if (e is BinaryExpression)
      {
        BinaryExpression be = (BinaryExpression)e;
        return new BinaryExpression(be.NodeType, RewriteExpression(cb, be.Left), RewriteExpression(cb, be.Right));
      }

      if (e is UnaryExpression)
      {
        UnaryExpression ue = (UnaryExpression)e;
        if (ue.NodeType == AstNodeType.Convert)
        {
          return Ast.ConvertHelper(RewriteExpression(cb, ue.Operand), ue.Type);
        }
        return null;
      }

      if (e is TypeBinaryExpression)
      {
        TypeBinaryExpression tbe = (TypeBinaryExpression)e;
        return Ast.TypeIs(RewriteExpression(cb, tbe.Expression), tbe.TypeOperand);
      }
      return e;
    }

    protected internal static Expression GetAst(object args, CodeBlock cb, bool istailposition)
    {
      Cons c = args as Cons;
      if (c != null)
      {

        if (Builtins.IsSymbol(c.car))
        {
          SymbolId f = (SymbolId)c.car;

          Variable var = cb.Lookup(f);

          if (var != null && !assigns.ContainsKey(f))
          {
            var = null;
          }

          object m;

#if OPTIMIZATIONS

          CodeBlockExpression cbe;

          //// needs to do the same for overloads...
          if (SimpleGenerator.libraryglobals.TryGetValue(f, out cbe))
          {
            Expression[] ppp = GetAstList(c.cdr as Cons, cb);

            if (cbe.Block.ParameterCount < 9 && cbe.Block.ParameterCount == ppp.Length)
            {
              //inline here? we could for simple bodies, but we need to copy the entire structure
              if (!(cbe.Block.HasEnvironment || cbe.Block.IsClosure))
              {
                if (cbe.Block.Body is ReturnStatement)
                {
                  ReturnStatement rs = (ReturnStatement)cbe.Block.Body;

                  if (IsSimpleExpression(rs.Expression))
                  {
                    return InlineCall(cb, Ast.CodeBlockExpression(RewriteBody(cbe.Block), false), ppp);
                  }
                }
              }
              return CallNormal(cbe, ppp);
            }
          }

          // varargs
          if (SimpleGenerator.libraryglobalsX.TryGetValue(f, out cbe))
          {
            Expression[] ppp = GetAstList(c.cdr as Cons, cb);

            if (cbe.Block.ParameterCount < 9 && cbe.Block.ParameterCount - 1 <= ppp.Length)
            {
              //inline here?
              return CallVarArgs(cbe, ppp);
            }
          }

          // overloads
          CodeBlockDescriptor[] cbd;
          if (SimpleGenerator.libraryglobalsN.TryGetValue(f, out cbd))
          {
            Expression[] ppp = GetAstList(c.cdr as Cons, cb);

            foreach (CodeBlockDescriptor d in cbd)
            {
              if (d.codeblock.Block.ParameterCount < 9)
              {
                if (ppp.Length == d.arity || (d.varargs && ppp.Length > d.arity))
                {
                  if (d.varargs)
                  {
                    //inline here?
                    return CallVarArgs(d.codeblock, ppp);
                  }
                  else
                  {
                    //inline here?
                    return CallNormal(d.codeblock, ppp);
                  }
                }
              }
            }
          }

          if (f == SymbolTable.StringToId("call-with-values"))
          {
            Expression[] ppp = GetAstListNoCast(c.cdr as Cons, cb);
            if (ppp.Length == 2 && ppp[1] is MethodCallExpression)
            {
              MethodCallExpression consumer = ppp[1] as MethodCallExpression;

              if (ppp[0] is MethodCallExpression)
              {
                MethodCallExpression producer = ppp[0] as MethodCallExpression;
                if (consumer.Method == Closure_Make && producer.Method == Closure_Make)
                {
                  CodeBlockExpression ccbe = consumer.Arguments[1] as CodeBlockExpression;
                  CodeBlockExpression pcbe = producer.Arguments[1] as CodeBlockExpression;

                  pcbe.Block.Bind();
                  ccbe.Block.Bind();

                  if (ccbe.Block.ParameterCount == 0)
                  {
                    return InlineCall(cb, ccbe);
                  }
                  else if (ccbe.Block.ParameterCount == 1)
                  {
                    return InlineCall(cb, ccbe, InlineCall(cb, pcbe));
                  }
                  else
                  {
                    Variable values = cb.CreateTemporaryVariable((SymbolId)Builtins.GenSym("values"), typeof(object[]));

                    Expression valuesarr = Ast.Read(values);

                    Expression[] pppp = new Expression[ccbe.Block.ParameterCount];

                    for (int i = 0; i < pppp.Length; i++)
                    {
                      pppp[i] = Ast.ArrayIndex(Ast.Read(values), Ast.Constant(i));
                    }

                    return Ast.Comma(Ast.Void(Ast.Write(values, Ast.ComplexCallHelper(InlineCall(cb, pcbe), typeof(MultipleValues).GetMethod("ToArray")))), InlineCall(cb, ccbe, pppp));
                  }
                }
              }
              if (consumer.Method == Closure_Make)
              {
                CodeBlockExpression ccbe = consumer.Arguments[1] as CodeBlockExpression;
                ccbe.Block.Bind();

                Expression producer = ppp[0];

                Expression exx = Ast.ConvertHelper(producer, typeof(ICallable));

                MethodInfo callx = GetCallable(0);

                if (ccbe.Block.ParameterCount == 0)
                {
                  return InlineCall(cb, ccbe);
                }
                else if (ccbe.Block.ParameterCount == 1)
                {
                  return InlineCall(cb, ccbe, Ast.Call(exx, callx));
                }
                else
                {
                  Variable values = cb.CreateTemporaryVariable((SymbolId)Builtins.GenSym("values"), typeof(object[]));

                  Expression valuesarr = Ast.Read(values);

                  Expression[] pppp = new Expression[ccbe.Block.ParameterCount];

                  for (int i = 0; i < pppp.Length; i++)
                  {
                    pppp[i] = Ast.ArrayIndex(Ast.Read(values), Ast.Constant(i));
                  }

                  return Ast.Comma(Ast.Void(Ast.Write(values, Ast.ComplexCallHelper(Ast.Call(exx, callx), typeof(MultipleValues).GetMethod("ToArray")))), InlineCall(cb, ccbe, pppp));
                }
              }
            }
            else
            {
              ;
            }
          }

#endif
          // this can be enabled once builtins are auto CPS'd.
          // ok I tried, but there are issues still, not sure what
#if OPTIMIZATIONS
          // check for inline emitter
          InlineEmitter ie;
          if (TryGetInlineEmitter(f, out ie))
          {
#if CPS
            Expression result = ie(GetAstList((c.cdr as Cons).cdr as Cons, cb));
#else
            Expression result = ie(GetAstList(c.cdr as Cons, cb));
#endif
            // if null is returned, the method cannot be inlined
            if (result != null)
            {
              if (result.Type.IsValueType)
              {
                result = Ast.Convert(result, typeof(object));
              }
#if CPS
              Expression k = Ast.ConvertHelper(GetAst((c.cdr as Cons).car, cb) , typeof(ICallable));
              return Ast.Call(k, GetCallable(1), result);
#else
              return result;
#endif
            }
          }
#endif

          if (Context.Scope.TryLookupName(f, out m))
          {
            if (var == null)
            {
              IGenerator gh = m as IGenerator;
              if (gh != null)
              {
                if (!Parser.sourcemap.TryGetValue(c, out spanhint))
                {
                  spanhint = SourceSpan.None;
                }
                return gh.Generate(c.cdr, cb);
              }

              BuiltinMethod bf = m as BuiltinMethod;
              if (bf != null)
              {
                MethodBinder mb = bf.Binder;
                Expression[] pars = Array.ConvertAll(GetAstList(c.cdr as Cons, cb), e => Unwrap(e));

                if (bf.AllowConstantFold)
                {
                  bool constant = Array.TrueForAll(pars, e => e is ConstantExpression);

                  if (constant)
                  {
                    object[] cargs = Array.ConvertAll(pars, e => ((ConstantExpression)e).Value);
                    CallTarget0 disp = delegate
                    {
                      return bf.Call(cargs);
                    };
                    object result = Runtime.R6RS.Exceptions.WithExceptionHandler(
                      Runtime.Builtins.SymbolValue(SymbolTable.StringToId("values")),
                      Closure.Make(null, disp));

                    if (!(result is Exception))
                    {
                      return GetCons(result, cb);
                    }
                  }
                }

                Type[] types = GetExpressionTypes(pars);
                MethodCandidate mc = mb.MakeBindingTarget(CallType.None, types);
                if (mc != null)
                {
                  if (mc.Target.NeedsContext)
                  {
                    pars = ArrayUtils.Insert<Expression>(Ast.CodeContext(), pars);
                  }
                  MethodBase meth = mc.Target.Method;

                  return Ast.ComplexCallHelper(meth as MethodInfo, pars);
                }
              }

#if OPTIMIZATIONS
              Closure clos = m as Closure;
              if (clos != null && !SetGenerator.IsAssigned(f))
              {

                // no provision for varargs
                MethodInfo[] mis = clos.Targets;
                if (mis.Length > 0)
                {
                  MethodBinder mb = MethodBinder.MakeBinder(binder, SymbolTable.IdToString(f), mis, BinderType.Normal);

                  Expression[] pars = Array.ConvertAll(GetAstList(c.cdr as Cons, cb), e => Unwrap(e));

                  if (clos.AllowConstantFold)
                  {
                    bool constant = Array.TrueForAll(pars, e => e is ConstantExpression);

                    if (constant)
                    {
                      object[] cargs = Array.ConvertAll(pars, e => ((ConstantExpression)e).Value);
                      try
                      {
                        return Ast.Constant(clos.Call(cargs));
                      }
                      catch
                      {
                        // nothing we can do...
                      }
                    }
                  }

                  Type[] types = GetExpressionTypes(pars);
                  MethodCandidate mc = mb.MakeBindingTarget(CallType.None, types);
                  if (mc != null)
                  {
                    if (mc.Target.NeedsContext)
                    {
                      pars = ArrayUtils.Insert<Expression>(Ast.CodeContext(), pars);
                    }
                    MethodBase meth = mc.Target.Method;

                    return Ast.ComplexCallHelper(meth as MethodInfo, pars);
                  }
                }
                // check for overload thing
              }
#endif
            }
          }
        }

        Expression[] pp = GetAstList(c.cdr as Cons, cb);
        Expression ex = Unwrap(GetAst(c.car, cb));

        // a 'let'
        if (ex is MethodCallExpression)
        {
          MethodCallExpression mcexpr = (MethodCallExpression)ex;
          if (mcexpr.Method == Closure_Make)
          {
            CodeBlockExpression cbe = mcexpr.Arguments[1] as CodeBlockExpression;

            if (cbe.Block.ParameterCount == pp.Length)
            {
              return InlineCall(cb, cbe, istailposition, pp);
            }
          }
          // cater for varargs more efficiently, this does not seem to hit, probably needed somewhere else
          if (mcexpr.Method == Closure_MakeVarArgsX)
          {
            CodeBlockExpression cbe = mcexpr.Arguments[1] as CodeBlockExpression;

            if (pp.Length < 9 && cbe.Block.ParameterCount <= pp.Length)
            {
              return CallVarArgs(cbe, pp);
            }
          }
        }

        if (ex is ConstantExpression)
        {
          Builtins.SyntaxError(SymbolTable.StringToId("generator"), "expecting a procedure", c.car, c);
        }

        ex = Ast.ConvertHelper(ex, typeof(ICallable));
        
        MethodInfo call = GetCallable(pp.Length);

        Expression r = pp.Length > 8 ?
          Ast.Call(ex, call, Ast.NewArray(typeof(object[]), pp)) :
          Ast.Call(ex, call, pp);

        if (spanhint != SourceSpan.Invalid || spanhint != SourceSpan.None)
        {
          r.SetLoc(spanhint);
        }

        return r;
      }
      object[] v = args as object[];
      if (v != null)
      {
        return GetConsVector(v, cb);
      }
      else if (args is byte[])
      {
        Expression[] ba = Array.ConvertAll<byte, Expression>(args as byte[], delegate (byte b) { return Ast.Constant(b);});
        return Ast.NewArray(typeof(byte[]), ba);
      }
      else
      {
        if (args is SymbolId)
        {
          SymbolId sym = (SymbolId)args;
          if (sym == SymbolTable.StringToId("uninitialized"))
          {
            return Ast.ReadField(null, typeof(Uninitialized), "Instance");
          }
          else
          {
            return Read(sym, cb, typeof(object));
          }
        }
        if (args == Builtins.Unspecified)
        {
          return Ast.ReadField(null, Unspecified);
        }
        if (args is Fraction)
        {
          Fraction f = (Fraction)args;
          return Ast.Constant( new FractionConstant(f));
        }
        if (args != null && args.GetType().Name == "stx")
        {
          args = new SerializedConstant(args);
        }
        return Ast.Constant(args);
      }
    }

    protected static Expression InlineCall(CodeBlock parent, CodeBlockExpression cbe, params Expression[] pp)
    {
      return InlineCall(parent, cbe, false, pp);
    }

    protected static Expression InlineCall(CodeBlock parent, CodeBlockExpression cbe, bool istailpostion, params Expression[] pp)
    {
      // all var names are unique.
      CodeBlock cb = cbe.Block;

      if (parent.IsGlobal) 
      {
        return CallNormal(cbe, pp);
      }

      List<Statement> assigns = new List<Statement>();
      int i = 0;

      cb.Inlined = true;

      foreach (Variable p in cb.Parameters)
      {
        p.Name = (SymbolId) Builtins.GenSym(p.Name);
        p.Block = parent;
        p.Kind = Variable.VariableKind.Local;
        parent.AddVariable(p);
        assigns.Add(Ast.Write(p, pp[i]));
        if (p.Lift)
        {
          parent.HasEnvironment = true;
        }
        i++;
      }

      foreach (Variable l in cb.Variables)
      {
        l.Name = (SymbolId) Builtins.GenSym(l.Name);
        l.Block = parent;
        parent.AddVariable(l);
        if (l.Lift)
        {
          parent.HasEnvironment = true;
        }
      }

      Expression body = RewriteReturn(cb.Body);

      if (assigns.Count > 0)
      {
        return Ast.Comma(Ast.Void(Ast.Block(assigns)), body);
      }
      else
      {
        return body;
      }
    }

    static Statement FlattenStatement(Statement s)
    {
      if (s is BlockStatement)
      {
        BlockStatement bs = (BlockStatement)s;
        if (bs.Statements.Count == 1)
        {
          return bs.Statements[0];
        }
      }
      return s;
    }

    static Expression RewriteReturn(Statement statement)
    {
      if (statement is BlockStatement)
      {
        BlockStatement bs = (BlockStatement)statement;
        List<Statement> newbody = new List<Statement>(bs.Statements);
        Statement last = newbody[newbody.Count - 1];
        
        newbody.RemoveAt(newbody.Count - 1);

        Statement fb = FlattenStatement(Ast.Block(newbody));

        Expression eb = Ast.Void(fb);

        if (fb is ExpressionStatement)
        {
          eb = ((ExpressionStatement)fb).Expression;
        }

        return Ast.Comma(eb, RewriteReturn(last));
      }

      if (statement is ReturnStatement)
      {
        Expression e = ((ReturnStatement)statement).Expression;
        if (e is MethodCallExpression)
        {
          ((MethodCallExpression)e).TailCall = false;
        }
        return e;
      }

      if (statement is IfStatement)
      {
        IfStatement ifs = (IfStatement)statement;

        Debug.Assert(ifs.Tests.Count == 1);

        return Ast.Condition(ifs.Tests[0].Test, RewriteReturn(ifs.Tests[0].Body), RewriteReturn(ifs.ElseStatement));
      }

      throw new ArgumentException("Unexpected");
    }


    #region Optimized calls

    static bool TryGetInlineEmitter(SymbolId f, out InlineEmitter ie)
    {
      ie = null;
      OptimizationLevel o = Optimization;
      while (o >= 0)
      {
        if (inlineemitters[o].TryGetValue(f, out ie))
        {
          return true;
        }
        o--;
      }
      return false;
    }



    protected static Expression CallNormal(CodeBlockExpression cbe, params Expression[] ppp)
    {
      bool needscontext = NeedsContext(cbe); // true;
      int pc = ppp.Length;
      MethodInfo dc = GetDirectCallable(needscontext, pc);

      List<Variable> paruninit = new List<Variable>(cbe.Block.Parameters);

      for (int i = 0; i < ppp.Length; i++)
      {
        if (ppp[i].Type == typeof(Uninitialized))
        {
          paruninit[i].SetUnInitialized();
        }
      }

      if (needscontext)
      {
        ppp = ArrayUtils.Insert<Expression>(Ast.CodeContext(), ppp);
      }

      cbe = Ast.CodeBlockReference(cbe.Block, CallTargets.GetTargetType(needscontext, pc, false));

      cbe.Block.Bind();

      return Ast.ComplexCallHelper(cbe, dc, ppp);
    }

    static bool NeedsContext(CodeBlockExpression cbe)
    {
      return cbe.Block.IsClosure || 
        cbe.Block.ExplicitCodeContextExpression == null && 
        (cbe.Block.Parent != null && !cbe.Block.Parent.IsGlobal);
    }

    protected static Expression CallVarArgs(CodeBlockExpression cbe, Expression[] ppp)
    {
      bool needscontext = NeedsContext(cbe); //true;

      int pc = cbe.Block.ParameterCount;

      Expression[] tail = new Expression[ppp.Length - (pc - 1)];

      Array.Copy(ppp, ppp.Length - tail.Length, tail, 0, tail.Length);

      Expression[] nppp = new Expression[pc];

      Array.Copy(ppp, nppp, ppp.Length - tail.Length);

      if (tail.Length > 0)
      {
        nppp[nppp.Length - 1] = Ast.ComplexCallHelper(MakeList(tail, true), tail);
      }
      else
      {
        nppp[nppp.Length - 1] = Ast.Null();
      }

      ppp = nppp;

      MethodInfo dc = GetDirectCallable(needscontext, pc);
      if (needscontext)
      {
        ppp = ArrayUtils.Insert<Expression>(Ast.CodeContext(), ppp);
      }

      cbe = Ast.CodeBlockReference(cbe.Block, CallTargets.GetTargetType(needscontext, pc, false));

      cbe.Block.Bind();

      return Ast.ComplexCallHelper(cbe, dc, ppp);
    }

    #endregion

    protected static Expression Unwrap(Expression ex)
    {
      while (ex is UnaryExpression && ((UnaryExpression)ex).NodeType == AstNodeType.Convert)
      {
        ex = ((UnaryExpression)ex).Operand;
      }

      return ex;
    }

  }

}
