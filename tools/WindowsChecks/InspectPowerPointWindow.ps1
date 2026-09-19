param([Parameter(Mandatory = $true)][long] $Hwnd)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
public static class WorkBookmarkPowerPointInspector {
 delegate bool EnumProc(IntPtr hwnd, IntPtr param);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr root, EnumProc proc, IntPtr data);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
 [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
 static string Class(IntPtr hwnd) { var text=new StringBuilder(256); GetClassName(hwnd,text,text.Capacity); return text.ToString(); }
 static object Read(object obj, string name) { return obj.GetType().InvokeMember(name,BindingFlags.GetProperty|BindingFlags.OptionalParamBinding,null,obj,new object[0],CultureInfo.GetCultureInfo("en-US")); }
 static long Identity(object obj) { var p=Marshal.GetIUnknownForObject(obj); try{return p.ToInt64();}finally{Marshal.Release(p);} }
 public static object Inspect(long raw) {
  var hwnd = new IntPtr(raw); uint pid; GetWindowThreadProcessId(hwnd,out pid);
  if(!IsWindow(hwnd)||Class(hwnd)!="PPTFrameClass") throw new Exception("Requested PowerPoint root no longer exists.");
  var classes=new Dictionary<string,int>(); var panes=new List<object>();
  EnumChildWindows(hwnd,delegate(IntPtr child,IntPtr ignored) {
   string cls=Class(child); string kind=cls+":"+(IsWindowVisible(child)?"visible":"hidden");
   if(!classes.ContainsKey(kind)) classes[kind]=0; classes[kind]++;
   if(cls!="paneClassDC" && cls!="mdiClass") return true;
   uint childPid; GetWindowThreadProcessId(child,out childPid);
   var info=new Dictionary<string,object>(); info["className"]=cls; info["hwnd"]=child.ToInt64(); info["visible"]=IsWindowVisible(child); info["pid"]=childPid;
   try {
    object window; var iid=new Guid("00020400-0000-0000-C000-000000000046");
    int hr=AccessibleObjectFromWindow(child,unchecked((uint)-16),ref iid,out window); info["hresult"]=hr;
    if(hr>=0) {
     info["windowIdentity"]=Identity(window); var app=Read(window,"Application"); info["appIdentity"]=Identity(app);
     info["activeWindowIdentity"]=Identity(Read(app,"ActiveWindow"));
     var presentation=Read(window,"Presentation"); info["presentationIdentity"]=Identity(presentation);
     info["path"]=Read(presentation,"FullName"); info["viewType"]=Read(window,"ViewType");
     try {info["reportedHwnd"]=Read(window,"HWND");}catch(Exception ex){info["hwndReadError"]=ex.GetType().Name+":"+ex.HResult;}
    }
   } catch(Exception ex){info["error"]=ex.GetType().Name+":"+ex.HResult;}
   panes.Add(info); return true;
  },IntPtr.Zero);
  return new {root=raw,pid=pid,classes=classes,panes=panes};
 }
}
'@
[WorkBookmarkPowerPointInspector]::Inspect($Hwnd) | ConvertTo-Json -Depth 8
