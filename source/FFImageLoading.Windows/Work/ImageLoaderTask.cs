﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FFImageLoading.Cache;
using FFImageLoading.Helpers;
using FFImageLoading.Extensions;
using FFImageLoading.DataResolver;
using System.Threading;

#if SILVERLIGHT
using System.Windows.Controls;
using System.Windows.Media.Imaging;
#else
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
#endif

namespace FFImageLoading.Work
{
    public class ImageLoaderTask : ImageLoaderTaskBase
    {
        internal readonly ITarget<WriteableBitmap, ImageLoaderTask> _target;

        public ImageLoaderTask(IDownloadCache downloadCache, IMainThreadDispatcher mainThreadDispatcher, IMiniLogger miniLogger, TaskParameter parameters, ITarget<WriteableBitmap, ImageLoaderTask> target, bool clearCacheOnOutOfMemory)
            : base(mainThreadDispatcher, miniLogger, parameters, false, clearCacheOnOutOfMemory)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            _target = target;

            DownloadCache = downloadCache;
        }

        protected IDownloadCache DownloadCache { get; private set; }

        public override bool UsesSameNativeControl(IImageLoaderTask task)
        {
            return _target.UsesSameNativeControl((ImageLoaderTask)task);
        }

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

        protected override async Task<GenerateResult> TryGeneratingImageAsync()
        {
            WithLoadingResult<WriteableBitmap> imageWithResult;
            WriteableBitmap image = null;

            try
            {
                imageWithResult = await RetrieveImageAsync(Parameters.Path, Parameters.Source, false).ConfigureAwait(false);
                image = imageWithResult.Item;
            }
            catch (Exception ex)
            {
                Logger.Error("An error occured while retrieving image.", ex);
                imageWithResult = new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                image = null;
            }

            if (image == null)
            {
                await LoadPlaceHolderAsync(Parameters.ErrorPlaceholderPath, Parameters.ErrorPlaceholderSource, false).ConfigureAwait(false);
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

                int pixelWidth = 0;
                int pixelHeight = 0;

                await MainThreadDispatcher.PostAsync(() =>
                {
					if (IsCancelled)
						return;
                    _target.Set(this, value, true, false);
 
                    pixelWidth = value.PixelWidth;
                    pixelHeight = value.PixelHeight;

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

        public override async Task<GenerateResult> LoadFromStreamAsync(Stream stream)
        {
            if (stream == null)
                return GenerateResult.Failed;

			if (IsCancelled)
                return GenerateResult.Canceled;

            WithLoadingResult<WriteableBitmap> resultWithImage;
            WriteableBitmap image = null;
            try
            {
                resultWithImage = await GetImageAsync("Stream", ImageSource.Stream, false, stream).ConfigureAwait(false);
                image = resultWithImage.Item;
            }
            catch (Exception ex)
            {
                Logger.Error("An error occured while retrieving image.", ex);
                resultWithImage = new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
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
                int pixelWidth = 0;
                int pixelHeight = 0;

                // Post on main thread
                await MainThreadDispatcher.PostAsync(() =>
                {
					if (IsCancelled)
                        return;

                    _target.Set(this, image, true, false);
                    pixelWidth = image.PixelWidth;
                    pixelHeight = image.PixelHeight;
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

        private async Task<WithLoadingResult<Stream>> GetStreamAsync(string path, ImageSource source)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new WithLoadingResult<Stream>(LoadingResult.Failed);

            try
            {
                using (var resolver = DataResolverFactory.GetResolver(source, Parameters, DownloadCache))
                {
                    return await resolver.GetStream(path, CancellationToken.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug(string.Format("Image request for {0} got cancelled.", path));
                return new WithLoadingResult<Stream>(LoadingResult.Canceled);
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to retrieve image data", ex);
                return new WithLoadingResult<Stream>(LoadingResult.Failed);
            }
        }

        protected virtual async Task<WithLoadingResult<WriteableBitmap>> GetImageAsync(string path, ImageSource source,
            bool isPlaceholder, Stream originalStream = null)
        {
            if (IsCancelled)
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);

            if (IsCancelled)
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);

            Stream stream = null;
            WithLoadingResult<Stream> streamWithResult;
            if (originalStream != null)
            {
                streamWithResult = new WithLoadingResult<Stream>(originalStream, LoadingResult.Stream);
            }
            else
            {
                streamWithResult = await GetStreamAsync(path, source).ConfigureAwait(false);
            }

            if (streamWithResult.HasError)
            {
                if (streamWithResult.Result == LoadingResult.NotFound)
                {
                    Logger.Error(string.Format("Not found: {0} from {1}", path, source.ToString()));
                }

                return new WithLoadingResult<WriteableBitmap>(streamWithResult.Result);
            }

            stream = streamWithResult.Item;

            if (IsCancelled)
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);

            try
            {
                try
                {
                    if (stream.Position != 0 && !stream.CanSeek)
                    {
                        if (originalStream != null)
                        {
                            // If we cannot seek the original stream then there's not much we can do
                            return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                        }
                        else
                        {
                            // Assets stream can't be seeked to origin position
                            stream.Dispose();
                            streamWithResult = await GetStreamAsync(path, source).ConfigureAwait(false);

                            if (streamWithResult.HasError)
                            {
                                return new WithLoadingResult<WriteableBitmap>(streamWithResult.Result);
                            }

                            stream = streamWithResult.Item;
                        }
                    }
                    else
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                    }

                    if (IsCancelled)
                        return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);
                }
                catch (Exception ex)
                {
                    Logger.Error("Something wrong happened while asynchronously retrieving image size from file: " + path, ex);
                    return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                }

                WriteableBitmap writableBitmap = null;

                // Special case to handle WebP decoding
                if (path.ToLowerInvariant().EndsWith(".webp"))
                {
                    //TODO
                    Logger.Error("Webp is not implemented on Windows");
                    return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                }

                // Setting image informations
                var imageInformation = streamWithResult.ImageInformation ?? new ImageInformation();
                imageInformation.SetKey(path == "Stream" ? GetKey() : GetKey(path), Parameters.CustomCacheKey);

                bool transformPlaceholdersEnabled = Parameters.TransformPlaceholdersEnabled.HasValue ?
                    Parameters.TransformPlaceholdersEnabled.Value : ImageService.Instance.Config.TransformPlaceholders;

                if (Parameters.Transformations != null && Parameters.Transformations.Count > 0
                && (!isPlaceholder || (isPlaceholder && transformPlaceholdersEnabled)))
                {
                    BitmapHolder imageIn = null;

                    try
                    {
                        imageIn = await stream.ToBitmapHolderAsync(Parameters.DownSampleSize, Parameters.DownSampleUseDipUnits, Parameters.DownSampleInterpolationMode, imageInformation).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Something wrong happened while asynchronously loading/decoding image: " + path, ex);
                        return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                    }

                    foreach (var transformation in Parameters.Transformations.ToList() /* to prevent concurrency issues */)
                    {
                        if (IsCancelled)
                            return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);

                        try
                        {
                            var old = imageIn;

                            IBitmap bitmapHolder = transformation.Transform(imageIn);
                            imageIn = bitmapHolder.ToNative();

                            if (old != null && old != imageIn && old.Pixels != imageIn.Pixels)
                            {
                                old.FreePixels();
                                old = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Can't apply transformation " + transformation.Key + " to image " + path, ex);
                        }
                    }

                    writableBitmap = await imageIn.ToBitmapImageAsync();
                    imageIn.FreePixels();
                    imageIn = null;
                }
                else
                {
                    try
                    {
                        writableBitmap = await stream.ToBitmapImageAsync(Parameters.DownSampleSize, Parameters.DownSampleUseDipUnits, Parameters.DownSampleInterpolationMode, imageInformation);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Something wrong happened while asynchronously loading/decoding image: " + path, ex);
                        return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);
                    }

                }
            
                return WithLoadingResult.Encapsulate(writableBitmap, streamWithResult.Result, imageInformation);
            }
            finally
            {
                if (stream != null)
                    stream.Dispose();
            }
        }
        
        private async Task<bool> LoadPlaceHolderAsync(string placeholderPath, ImageSource source, bool isLoadingPlaceholder)
        {
            if (string.IsNullOrWhiteSpace(placeholderPath))
                return false;

            var cacheEntry = ImageCache.Instance.Get(GetKey(placeholderPath));
            WriteableBitmap image = cacheEntry == null ? null : cacheEntry.Item1;

            bool isLocalOrFromCache = true;

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

        private async Task<WithLoadingResult<WriteableBitmap>> RetrieveImageAsync(string sourcePath, ImageSource source, bool isPlaceholder)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.Failed);

            // If the image cache is available and this task has not been cancelled by another
            // thread and the ImageView that was originally bound to this task is still bound back
            // to this task and our "exit early" flag is not set then try and fetch the bitmap from
            // the cache
            if (IsCancelled || ImageService.Instance.ExitTasksEarly)
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.Canceled);

            if (!_target.IsTaskValid(this))
                return new WithLoadingResult<WriteableBitmap>(LoadingResult.InvalidTarget);

            var resultWithImage = await GetImageAsync(sourcePath, source, isPlaceholder).ConfigureAwait(false);

            if (resultWithImage.HasError)
                return resultWithImage;

            // FMT: even if it was canceled, if we have the bitmap we add it to the cache
            ImageCache.Instance.Add(GetKey(sourcePath), resultWithImage.ImageInformation, resultWithImage.Item);

            return resultWithImage;
        }
    }
}