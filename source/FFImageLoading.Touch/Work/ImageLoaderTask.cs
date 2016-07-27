﻿using System;
using FFImageLoading.Cache;
using FFImageLoading.Helpers;
using UIKit;
using System.Threading.Tasks;
using Foundation;
using FFImageLoading.Work.DataResolver;
using System.Linq;
using System.IO;
using FFImageLoading.Extensions;

namespace FFImageLoading.Work
{
	public class ImageLoaderTask : ImageLoaderTaskBase
	{
		internal readonly ITarget<UIImage, ImageLoaderTask> _target;
		
		static ImageLoaderTask()
		{
			// do not remove!
			// kicks scale retrieval so it's available for all, without deadlocks due to accessing MainThread
			#pragma warning disable 0219
			var ignore = ScaleHelper.Scale;
			#pragma warning restore 0219
		}

		public ImageLoaderTask(IDownloadCache downloadCache, IMainThreadDispatcher mainThreadDispatcher, IMiniLogger miniLogger, TaskParameter parameters, nfloat imageScale, ITarget<UIImage, ImageLoaderTask> target, bool clearCacheOnOutOfMemory)
			: base(mainThreadDispatcher, miniLogger, parameters, true, clearCacheOnOutOfMemory)
		{
			if (target == null)
				throw new ArgumentNullException(nameof(target));
			
			_target = target;
			DownloadCache = downloadCache;
		}

		protected IDownloadCache DownloadCache { get; private set; }

		/// <summary>
		/// Indicates if the task uses the same native control
		/// </summary>
		/// <returns><c>true</c>, if same native control is used, <c>false</c> otherwise.</returns>
		/// <param name="task">Task to check.</param>
		public override bool UsesSameNativeControl(IImageLoaderTask task)
		{
			return _target.UsesSameNativeControl((ImageLoaderTask)task);
		}

		/// <summary>
		/// Prepares the instance before it runs.
		/// </summary>
		public override async Task<bool> PrepareAndTryLoadingFromCacheAsync()
		{
			if (CanUseMemoryCache())
			{
				var cacheResult = await TryLoadingFromCacheAsync().ConfigureAwait(false);
				if (cacheResult == CacheResult.Found || cacheResult == CacheResult.ErrorOccured) // If image is loaded from cache there is nothing to do here anymore, if something weird happened with the cache... error callback has already been called, let's just leave
					return true; // stop processing if loaded from cache OR if loading from cached raised an exception
			}

			await LoadPlaceHolderAsync(Parameters.LoadingPlaceholderPath, Parameters.LoadingPlaceholderSource, true).ConfigureAwait(false);
			return false;
		}

		/// <summary>
		/// Runs the image loading task: gets image from file, url, asset or cache. Then assign it to the imageView.
		/// </summary>
		protected override async Task<GenerateResult> TryGeneratingImageAsync()
		{
			WithLoadingResult<UIImage> imageWithResult;
			UIImage image = null;
			try
			{
				imageWithResult = await RetrieveImageAsync(Parameters.Path, Parameters.Source, false).ConfigureAwait(false);
				image = imageWithResult.Item;
			}
			catch (Exception ex)
			{
				Logger.Error("An error occured while retrieving image.", ex);
				imageWithResult = new WithLoadingResult<UIImage>(LoadingResult.Failed);
				image = null;
			}

			if (image == null)
			{
				await LoadPlaceHolderAsync(Parameters.ErrorPlaceholderPath, Parameters.ErrorPlaceholderSource, true).ConfigureAwait(false);
				return imageWithResult.GenerateResult;
			}

			if (IsCancelled)
				return GenerateResult.Canceled;

			if (!_target.IsTaskValid(this))
				return GenerateResult.InvalidTarget;

			try
			{
				// Post on main thread
				await MainThreadDispatcher.PostAsync(() =>
					{
						if (IsCancelled)
							return;

						_target.Set(this, image, imageWithResult.Result.IsLocalOrCachedResult(), false);
						Completed = true;
						Parameters?.OnSuccess(imageWithResult.ImageInformation, imageWithResult.Result);
					}).ConfigureAwait(false);

				if (!Completed)
					return GenerateResult.Failed;
			}
			catch (Exception ex2)
			{
				await LoadPlaceHolderAsync(Parameters.ErrorPlaceholderPath, Parameters.ErrorPlaceholderSource, false).ConfigureAwait(false);
				throw ex2;
			}

			return GenerateResult.Success;
		}

		/// <summary>
		/// Tries to load requested image from the cache asynchronously.
		/// </summary>
		/// <returns>A boolean indicating if image was loaded from cache.</returns>
		public override async Task<CacheResult> TryLoadingFromCacheAsync()
		{
			try
			{
				if (!_target.IsValid)
					return CacheResult.NotFound; // weird situation, dunno what to do

				var cacheEntry = ImageCache.Instance.Get(GetKey());
				if (cacheEntry == null)
					return CacheResult.NotFound; // not available in the cache

				var value = cacheEntry.Item1;

				if (value == null)
					return CacheResult.NotFound; // not available in the cache

				if (IsCancelled)
					return CacheResult.NotFound; // not sure what to return in that case

				await MainThreadDispatcher.PostAsync(() =>
					{
						if (IsCancelled)
							return;
						
						_target.Set(this, value, true, false);
						Completed = true;
						Parameters?.OnSuccess(cacheEntry.Item2, LoadingResult.MemoryCache);
					}).ConfigureAwait(false);

				if (!Completed)
					return CacheResult.NotFound; // not sure what to return in that case

				return CacheResult.Found; // found and loaded from cache
			}
			catch (Exception ex)
			{
				Parameters?.OnError(ex);
				return CacheResult.ErrorOccured; // weird, what can we do if loading from cache fails
			}
		}

		/// <summary>
		/// Loads the image from given stream asynchronously.
		/// </summary>
		/// <returns>An awaitable task.</returns>
		/// <param name="stream">The stream to get data from.</param>
		public override async Task<GenerateResult> LoadFromStreamAsync(Stream stream)
		{
			if (stream == null)
				return GenerateResult.Failed;

			if (IsCancelled)
				return GenerateResult.Canceled;

			WithLoadingResult<UIImage> resultWithImage;
			UIImage image = null;
			try
			{
				resultWithImage = await GetImageAsync("Stream", ImageSource.Stream, false, stream).ConfigureAwait(false);
				image = resultWithImage.Item;
			}
			catch (Exception ex)
			{
				Logger.Error("An error occured while retrieving image.", ex);
				resultWithImage = new WithLoadingResult<UIImage>(LoadingResult.Failed);
				image = null;
			}

			if (image == null)
			{
				await LoadPlaceHolderAsync(Parameters.ErrorPlaceholderPath, Parameters.ErrorPlaceholderSource, false).ConfigureAwait(false);
				return resultWithImage.GenerateResult;
			}

			if (CanUseMemoryCache())
			{
				ImageCache.Instance.Add(GetKey(), resultWithImage.ImageInformation, image);
			}

			if (IsCancelled)
				return GenerateResult.Canceled;

			if (!_target.IsTaskValid(this))
				return GenerateResult.InvalidTarget;

			try
			{
				// Post on main thread
				await MainThreadDispatcher.PostAsync(() =>
					{
						if (IsCancelled)
							return;

						_target.Set(this, image, true, false);
						Completed = true;
						Parameters?.OnSuccess(resultWithImage.ImageInformation, resultWithImage.Result);
					}).ConfigureAwait(false);

				if (!Completed)
					return GenerateResult.Failed;
			}
			catch (Exception ex2)
			{
				await LoadPlaceHolderAsync(Parameters.ErrorPlaceholderPath, Parameters.ErrorPlaceholderSource, false).ConfigureAwait(false);
				throw ex2;
			}

			return GenerateResult.Success;
		}

		protected virtual async Task<WithLoadingResult<UIImage>> GetImageAsync(string sourcePath, ImageSource source, 
			bool isPlaceholder, Stream originalStream = null)
		{
			if (IsCancelled)
				return new WithLoadingResult<UIImage>(LoadingResult.Canceled);

			LoadingResult? result = null;
			UIImage image = null;
			byte[] bytes = null;
			string path = sourcePath;
			ImageInformation imageInformation = null;

			try
			{
				if (originalStream != null)
				{
					try
					{
						// check is stream is memorystream
						var ms = originalStream as MemoryStream;
						if (ms != null)
						{
							bytes = ms.ToArray();
						}
						else if (originalStream.CanSeek)
						{
							bytes = new byte[originalStream.Length];
							await originalStream.ReadAsync(bytes, 0, (int)originalStream.Length, CancellationToken.Token).ConfigureAwait(false);
						}
						else
						{
							using (var ms2 = new MemoryStream())
							{
								await originalStream.CopyToAsync(ms2).ConfigureAwait(false);
								bytes = ms2.ToArray();
							}
						}

						path = sourcePath;
						result = LoadingResult.Stream;
					}
					finally
					{
						originalStream.Dispose();
					}
				}
				else
				{
					using (var resolver = DataResolverFactory.GetResolver(source, Parameters, DownloadCache, MainThreadDispatcher))
					{
						var data = await resolver.GetData(path, CancellationToken.Token).ConfigureAwait(false);
						if (data == null)
							return new WithLoadingResult<UIImage>(LoadingResult.Failed);

						image = data.Image;
						bytes = data.Data;
						path = data.ResultIdentifier;
						result = data.Result;
						imageInformation = data.ImageInformation;
					}
				}
			}
			catch (OperationCanceledException)
			{
				Logger.Debug(string.Format("Image request for {0} got cancelled.", path));
				return new WithLoadingResult<UIImage>(LoadingResult.Canceled);
			}
			catch (Exception ex)
			{
				var message = String.Format("Unable to retrieve image data from source: {0}", sourcePath);
				Logger.Error(message, ex);
				Parameters.OnError(ex);
				return new WithLoadingResult<UIImage>(LoadingResult.Failed);
			}

			if (bytes == null && image == null)
			{
				if (result != null && (int)result<0) // it's below zero if it's an error
					return new WithLoadingResult<UIImage>(result.Value);
				else
					return new WithLoadingResult<UIImage>(LoadingResult.Failed);
			}

			if (IsCancelled)
				return new WithLoadingResult<UIImage>(LoadingResult.Canceled);

			UIImage imageIn = image;
			NSData nsdata = null;

			if (imageIn == null)
			{
				// Special case to handle WebP decoding on iOS
				if (sourcePath.ToLowerInvariant().EndsWith(".webp", StringComparison.InvariantCulture))
				{
					imageIn = new WebP.Touch.WebPCodec().Decode(bytes);
				}
				else
				{
					// nfloat scale = _imageScale >= 1 ? _imageScale : ScaleHelper.Scale;
					nsdata = NSData.FromArray(bytes);
					if (nsdata == null)
						return new WithLoadingResult<UIImage>(LoadingResult.Failed);
				}
			}

			bytes = null;

			// Setting image informations
			if (imageInformation == null)
				imageInformation = new ImageInformation();
			
			imageInformation.SetKey(path == "Stream" ? GetKey() : GetKey(sourcePath), Parameters.CustomCacheKey);

			// We rely on ImageIO for all datasources except AssetCatalog, this way we don't generate temporary UIImage
			// furthermore we can do all the work in a thread safe way and in threadpool
			if (nsdata != null)
			{
				int downsampleWidth = Parameters.DownSampleSize?.Item1 ?? 0;
				int downsampleHeight = Parameters.DownSampleSize?.Item2 ?? 0;

				if (Parameters.DownSampleUseDipUnits)
				{
					downsampleWidth = downsampleWidth.PointsToPixels();
					downsampleHeight = downsampleHeight.PointsToPixels();
				}

				imageIn = nsdata.ToImage(new CoreGraphics.CGSize(downsampleWidth, downsampleHeight), ScaleHelper.Scale, NSDataExtensions.RCTResizeMode.ScaleAspectFill, imageInformation);
			}
			else if (Parameters.DownSampleSize != null && imageIn != null)
			{
				// if we already have the UIImage in memory it doesn't really matter to resize it
				// furthermore this will only happen for AssetCatalog images (yet)
			}
			
			bool transformPlaceholdersEnabled = Parameters.TransformPlaceholdersEnabled.HasValue ? 
				Parameters.TransformPlaceholdersEnabled.Value : ImageService.Instance.Config.TransformPlaceholders;

			if (Parameters.Transformations != null && Parameters.Transformations.Count > 0 
				&& (!isPlaceholder || (isPlaceholder && transformPlaceholdersEnabled)))
			{
				foreach (var transformation in Parameters.Transformations.ToList() /* to prevent concurrency issues */)
				{
					if (IsCancelled)
						return new WithLoadingResult<UIImage>(LoadingResult.Canceled);

					try
					{
						var old = imageIn;
						var bitmapHolder = transformation.Transform(new BitmapHolder(imageIn));
						imageIn = bitmapHolder.ToNative();

						// Transformation succeeded, so garbage the source
						if (old != null && old != imageIn && old.Handle != imageIn.Handle)
							old.Dispose();
					}
					catch (Exception ex)
					{
						Logger.Error("Can't apply transformation " + transformation.Key + " to image " + path, ex);
					}
				}
			}
				
			return WithLoadingResult.Encapsulate(imageIn, result.Value, imageInformation);
		}

		/// <summary>
		/// Loads given placeHolder into the imageView.
		/// </summary>
		/// <returns>An awaitable task.</returns>
		/// <param name="placeholderPath">Full path to the placeholder.</param>
		/// <param name="source">Source for the path: local, web, assets</param>
		private async Task<bool> LoadPlaceHolderAsync(string placeholderPath, ImageSource source, bool isLoadingPlaceholder)
		{
			if (string.IsNullOrWhiteSpace(placeholderPath))
				return false;

			bool isLocalOrFromCache = true;

			var cacheEntry = ImageCache.Instance.Get(GetKey(placeholderPath));
			UIImage image = cacheEntry == null ? null: cacheEntry.Item1;

			if (image == null)
			{
				try
				{
					var imageWithResult = await RetrieveImageAsync(placeholderPath, source, true).ConfigureAwait(false);
					image = imageWithResult.Item;
					isLocalOrFromCache = imageWithResult.Result.IsLocalOrCachedResult();
				}
				catch (Exception ex)
				{
					Logger.Error("An error occured while retrieving placeholder's drawable.", ex);
					return false;
				}
			}

			if (image == null)
				return false;

			if (!_target.IsValid)
				return false;

			if (IsCancelled)
				return false;

			// Post on main thread but don't wait for it
			MainThreadDispatcher.Post(() => _target.Set(this, image, isLocalOrFromCache, isLoadingPlaceholder));

			return true;
		}

		private async Task<WithLoadingResult<UIImage>> RetrieveImageAsync(string sourcePath, ImageSource source, bool isPlaceholder)
		{
			// If the image cache is available and this task has not been cancelled by another
			// thread and the ImageView that was originally bound to this task is still bound back
			// to this task and our "exit early" flag is not set then try and fetch the bitmap from
			// the cache
			if (IsCancelled || ImageService.Instance.ExitTasksEarly)
				return new WithLoadingResult<UIImage>(LoadingResult.Canceled);

			if (!_target.IsTaskValid(this))
				return new WithLoadingResult<UIImage>(LoadingResult.InvalidTarget);

			var resultWithImage = await GetImageAsync(sourcePath, source, isPlaceholder).ConfigureAwait(false);
			if (resultWithImage.HasError)
			{
				return resultWithImage;
			}

			// FMT: even if it was canceled, if we have the bitmap we add it to the cache
			ImageCache.Instance.Add(GetKey(sourcePath), resultWithImage.ImageInformation, resultWithImage.Item);

			return resultWithImage;
		}
	}
}

