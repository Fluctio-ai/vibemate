# 托盘宫格素材归一化管线：品红/玫红底大图 → 规整 3x3 透明宫格 tray.icons.png
# 用法：.\make-tray-icons.ps1 [-Src 品红背景.png]     （在 _v2 目录下跑）
# 步骤：抠像（高R低G特征→透明，RGB清0防彩边）→ 逐格找内容包围盒 →
#       每个图标平移到格中心（只全局统一缩放，保留帧间大小差异=呼吸节奏）→ 512 输出
# 之后还要 deploy.ps1 重新发布（tray.icons.png 嵌在 exe 资源里）。
param([string]$Src = "品红背景.png")
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'VibeMate'
$srcPath = if ([System.IO.Path]::IsPathRooted($Src)) { $Src } else { Join-Path $dir $Src }

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class SheetNorm {
  public static Bitmap Process(string srcPath, int outSize, out string report) {
    using (var src = new Bitmap(srcPath)) {
      int W = src.Width, H = src.Height;
      var bd = src.LockBits(new Rectangle(0,0,W,H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      int bytes = bd.Stride * H;
      var buf = new byte[bytes];
      Marshal.Copy(bd.Scan0, buf, 0, bytes);
      src.UnlockBits(bd);
      // 1) 抠像：玫红背景特征（高R、极低G；实测 R 230-242 / G 5-13 / B 145-158）
      for (int i = 0; i < bytes; i += 4) {
        byte b = buf[i], g = buf[i+1], r = buf[i+2];
        if (r > 190 && g < 70 && (r - g) > 120 && (b - g) > 50) {
          buf[i] = 0; buf[i+1] = 0; buf[i+2] = 0; buf[i+3] = 0;
        } else buf[i+3] = 255;
      }
      var keyed = new Bitmap(W, H, PixelFormat.Format32bppArgb);
      var kd = keyed.LockBits(new Rectangle(0,0,W,H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      Marshal.Copy(buf, 0, kd.Scan0, bytes);
      keyed.UnlockBits(kd);
      // 2) 每格内容包围盒（素材 9 个图标位置不齐，按实际内容定位）
      int tw = W / 3, th = H / 3;
      var boxes = new Rectangle[3, 3];
      var sb = new StringBuilder();
      for (int col = 0; col < 3; col++)
        for (int row = 0; row < 3; row++) {
          int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
          for (int yy = row*th; yy < (row+1)*th; yy++)
            for (int xx = col*tw; xx < (col+1)*tw; xx++)
              if (buf[yy*bd.Stride + xx*4 + 3] > 8) {
                if (xx<minX) minX=xx; if (xx>maxX) maxX=xx;
                if (yy<minY) minY=yy; if (yy>maxY) maxY=yy;
              }
          boxes[col,row] = maxX<0 ? new Rectangle(col*tw+tw/4, row*th+th/4, tw/2, th/2)
                                  : Rectangle.FromLTRB(minX, minY, maxX+1, maxY+1);
          var bx = boxes[col,row];
          sb.AppendFormat("[{0},{1}] {2}x{3} 偏({4:F0},{5:F0})  ",
            col, row, bx.Width, bx.Height,
            bx.X+bx.Width/2f-(col*tw+tw/2f), bx.Y+bx.Height/2f-(row*th+th/2f));
        }
      report = sb.ToString();
      // 3) 归一化排版：每个图标平移到目标格中心（全局统一缩放）
      var dst = new Bitmap(outSize, outSize, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(dst)) {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float scale = (float)outSize / W;
        for (int col = 0; col < 3; col++)
          for (int row = 0; row < 3; row++) {
            var bx = boxes[col,row];
            float dw = bx.Width * scale, dh = bx.Height * scale;
            float gx = (col + 0.5f) * outSize / 3f, gy = (row + 0.5f) * outSize / 3f;
            g.DrawImage(keyed,
              new RectangleF(gx - dw/2f, gy - dh/2f, dw, dh),
              new RectangleF(bx.X, bx.Y, bx.Width, bx.Height), GraphicsUnit.Pixel);
          }
      }
      keyed.Dispose();
      return dst;
    }
  }
}
'@

$rep = ''
$dst = [SheetNorm]::Process($srcPath, 512, [ref]$rep)
$dst.Save((Join-Path $dir 'tray.icons.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$dst.Dispose()
Write-Host "归一化报告（偏移=素材原始漂移，已全部居中）："
Write-Host "  $rep"
Write-Host "已生成 tray.icons.png —— 记得 .\deploy.ps1 重新发布"
