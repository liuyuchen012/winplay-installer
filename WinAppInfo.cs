using System;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Java.Interop;

namespace Com.Xiaomi.Winplay.Bean
{
    /// <summary>
    /// 与小米 PC 引擎 (com.xiaomi.winplay) 内部的 WinAppInfo 同全限定名的 Parcelable。
    /// 跨进程传递时，Intent.PutExtra 会写入本类的全限定名；接收方 (WineActivity) 用它自己的
    /// 真实 WinAppInfo.Creator 反序列化，因此 write 顺序必须与真实 writeToParcel 完全一致：
    ///   writeString(exePath); writeInt(env.Count); [writeString(key); writeString(value)]*; writeInt(isUnixPath)
    /// </summary>
    [Register("com/xiaomi/winplay/bean/WinAppInfo", DoNotGenerateAcw = false)]
    public class WinAppInfo : Java.Lang.Object, IParcelable
    {
        public string ExePath { get; set; }
        public System.Collections.Generic.IDictionary<string, string> Env { get; set; }
        public bool IsUnixPath { get; set; }

        public WinAppInfo() : base()
        {
            Env = new System.Collections.Generic.Dictionary<string, string>();
        }

        public WinAppInfo(string exePath, System.Collections.Generic.IDictionary<string, string> env, bool isUnixPath) : base()
        {
            ExePath = exePath;
            Env = env ?? new System.Collections.Generic.Dictionary<string, string>();
            IsUnixPath = isUnixPath;
        }

        protected WinAppInfo(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public int DescribeContents() => 0;

        public void WriteToParcel(Parcel dest, ParcelableWriteFlags flags)
        {
            dest.WriteString(ExePath);
            dest.WriteInt(Env?.Count ?? 0);
            if (Env != null)
            {
                foreach (var kv in Env)
                {
                    dest.WriteString(kv.Key);
                    dest.WriteString(kv.Value);
                }
            }
            dest.WriteInt(IsUnixPath ? 1 : 0);
        }
    }
}
