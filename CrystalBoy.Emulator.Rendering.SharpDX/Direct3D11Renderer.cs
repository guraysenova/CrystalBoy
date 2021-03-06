﻿#if false

using System;
using System.ComponentModel;
using System.Windows.Forms;
using System.IO;
using SharpDX;
using SharpDX.Direct3D11;
using System.Runtime.InteropServices;
using CrystalBoy.Emulation;

namespace CrystalBoy.Emulator.Rendering.SharpDX
{
	[DisplayName("Direct3D 11")]
	[Description("Renders video using Direct3D 11 / SlimDX.")]
	public sealed class Direct3D11Renderer : VideoRenderer<Control>
	{
		Direct3D direct3D;
		PresentParameters presentParameters;
		Device device;
		Texture texture;
		VertexBuffer vertexBuffer;
		DataStream dataStream;
		TextureFilter textureFilter;
		byte[] temporaryBuffer;
		GCHandle temporaryBufferHandle;
		bool temporaryBufferLocked;

		public Direct3D11Renderer(Control renderObject)
			: base(renderObject)
		{
			direct3D = new Direct3D();
			ResetTextureFilter();
			FillPresentParameters();
			CreateDevice();
			CreateTexture();
			CreateVertexBuffer();
			ResetRenderStates();
			ResetTextureFilter();
			renderObject.SizeChanged += OnSizeChanged;
			temporaryBuffer = new byte[4 * 160 * 144];
		}

		public override void Dispose()
		{
			DisposeResources();
			DisposeDevice();
			RenderObject.SizeChanged -= OnSizeChanged;
		}

		private void DisposeResources()
		{
			DisposeVertexBuffer();
			DisposeTexture();
		}

		private void FillPresentParameters()
		{
			presentParameters = new PresentParameters();

			presentParameters.BackBufferFormat = Format.X8R8G8B8;
			presentParameters.PresentationInterval = PresentInterval.Immediate;
			presentParameters.PresentFlags = PresentFlags.None;
			presentParameters.SwapEffect = SwapEffect.Discard;
			presentParameters.Windowed = true;
			presentParameters.DeviceWindowHandle = RenderObject.Handle;
		}

		private void OnSizeChanged(object sender, EventArgs e) { ResetDevice(); }

		#region Device

		private void CreateDevice()
		{
			device = new Device(direct3D, 0, DeviceType.Hardware, RenderObject.Handle, CreateFlags.HardwareVertexProcessing | CreateFlags.FpuPreserve, presentParameters);
		}

		private bool ResetDevice()
		{
			DisposeResources();

			presentParameters.BackBufferWidth = 0;
			presentParameters.BackBufferHeight = 0;

			if (RenderObject.FindForm().WindowState == FormWindowState.Minimized) return false;

			if (device.Reset(presentParameters).IsSuccess)
			{
				CreateTexture();
				RefillTexture();
				CreateVertexBuffer();
				ResetRenderStates();
				ResetTextureFilter();

				return true;
			}
			else return false;
		}

		private void RefillTexture()
		{
			try
			{
				var dataRectangle = texture.LockRectangle(0, LockFlags.Discard);

				int rowLength = 160 * 4;
				int skip = dataRectangle.Pitch - rowLength;

				for (int i = 0; i < temporaryBuffer.Length; i += rowLength)
				{
					dataRectangle.Data.Write(temporaryBuffer, i, rowLength);
					dataRectangle.Data.Seek(skip, System.IO.SeekOrigin.Current);
				}

				dataRectangle.Data.Dispose();

				texture.UnlockRectangle(0);
			}
			catch (Direct3D9Exception) { }
		}

		private void DisposeDevice()
		{
			if (!device.Disposed)
				device.Dispose();
		}

		#endregion

		#region Vertex Buffer

		private void CreateVertexBuffer()
		{
			vertexBuffer = new VertexBuffer(device, TransformedTexturedVertex.StrideSize * 4, Usage.DoNotClip, VertexFormat.PositionRhw | VertexFormat.Texture1, Pool.Default);

			using (var dataStream = vertexBuffer.Lock(0, vertexBuffer.Description.SizeInBytes, LockFlags.Discard))
			{
				dataStream.Write(new TransformedTexturedVertex(new Vector4(device.Viewport.X - 0.5f, device.Viewport.Y - 0.5f, 0, 1), new Vector2(0, 0)));
				dataStream.Write(new TransformedTexturedVertex(new Vector4(device.Viewport.Width - 0.5f, device.Viewport.Y - 0.5f, 0, 1), new Vector2(1, 0)));
				dataStream.Write(new TransformedTexturedVertex(new Vector4(device.Viewport.X - 0.5f, device.Viewport.Height - 0.5f, 0, 1), new Vector2(0, 1)));
				dataStream.Write(new TransformedTexturedVertex(new Vector4(device.Viewport.Width - 0.5f, device.Viewport.Height - 0.5f, 0, 1), new Vector2(1, 1)));
			}

			vertexBuffer.Unlock();

			device.SetStreamSource(0, vertexBuffer, 0, TransformedTexturedVertex.StrideSize);
			device.VertexFormat = vertexBuffer.Description.FVF;
		}

		private void DisposeVertexBuffer()
		{
			if (vertexBuffer != null && !vertexBuffer.Disposed)
				vertexBuffer.Dispose();
			vertexBuffer = null;
		}

		#endregion

		#region Texture

		private void CreateTexture()
		{
			texture = new Texture(device, 160, 144, 1, Usage.Dynamic, Format.X8R8G8B8, Pool.Default);
			device.SetTexture(0, texture);
		}

		private void DisposeTexture()
		{
			if (texture != null && !texture.Disposed)
				texture.Dispose();
			texture = null;
		}

		#endregion

		#region Locking

		public override unsafe void* LockBorderBuffer(out int stride)
		{
			throw new NotImplementedException();
		}

		public override void UnlockBorderBuffer()
		{
			throw new NotImplementedException();
		}

		public override unsafe void* LockScreenBuffer(out int stride)
		{
			if (texture != null && device.TestCooperativeLevel().IsSuccess)
			{
				var dataRectangle = texture.LockRectangle(0, LockFlags.Discard);

				dataStream = dataRectangle.Data;

				stride = dataRectangle.Pitch;

				return dataStream.DataPointer.ToPointer();
			}
			else
			{
				if (!temporaryBufferLocked)
				{
					temporaryBufferHandle = GCHandle.Alloc(temporaryBuffer, GCHandleType.Pinned);
					temporaryBufferLocked = true;
				}

				stride = 160 * 4;

				return temporaryBufferHandle.AddrOfPinnedObject().ToPointer();
			}
		}

		public override void UnlockScreenBuffer()
		{
			if (temporaryBufferLocked)
			{
				temporaryBufferHandle.Free();
				temporaryBufferLocked = false;
			}
			else if (dataStream != null)
			{
				dataStream.Dispose();
				dataStream = null;
				if (device != null && device.TestCooperativeLevel().IsSuccess) texture.UnlockRectangle(0);
			}
		}

		#endregion

		#region Render States

		private void ResetRenderStates()
		{
			device.SetRenderState(RenderState.Clipping, false);
		}

		private void ResetTextureFilter()
		{
			textureFilter = Interpolation ? TextureFilter.Linear : TextureFilter.Point;

			if (device != null && device.TestCooperativeLevel().IsSuccess)
			{
				device.SetSamplerState(0, SamplerState.MagFilter, (int)textureFilter);
				device.SetSamplerState(0, SamplerState.MinFilter, (int)textureFilter);
			}
		}

		#endregion

		public override bool SupportsInterpolation { get { return true; } }

		protected override void OnInterpolationChanged(EventArgs e)
		{
			ResetTextureFilter();
			base.OnInterpolationChanged(e);
		}

		public override void Render()
		{
			var result = device.TestCooperativeLevel();

			if (result.IsSuccess)
			{
				device.Clear(ClearFlags.Target, 0, 1, 0);
				device.BeginScene();
				device.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
				device.EndScene();
				device.Present(Present.DoNotWait);
			}
			else if (result == ResultCode.DeviceLost) DisposeResources();
			else if (result == ResultCode.DeviceNotReset) { if (ResetDevice()) Render(); }
		}
	}
}

#endif