﻿using ExClient;
using ExViewer.Settings;
using ExViewer.Views;
using Newtonsoft.Json;
using System;
using static System.Runtime.InteropServices.WindowsRuntime.AsyncInfo;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Foundation;
using Windows.System;
using System.Collections.ObjectModel;
using Windows.Storage.Streams;
using Windows.Storage;
using Windows.Graphics.Imaging;
using ExClient.Api;
using Windows.ApplicationModel.DataTransfer;
using Opportunity.MvvmUniverse.Collections;
using Opportunity.MvvmUniverse.Commands;
using Opportunity.MvvmUniverse;
using ExClient.Tagging;
using ExClient.Galleries;
using ExClient.Galleries.Metadata;
using Windows.Storage.AccessCache;
using ExViewer.Helpers;

namespace ExViewer.ViewModels
{
    public enum OperationState
    {
        NotStarted,
        Started,
        Failed,
        Completed
    }

    public class GalleryVM : ViewModelBase
    {
        private static CacheStorage<GalleryInfo, GalleryVM> Cache
        {
            get;
        } = new CacheStorage<GalleryInfo, GalleryVM>(gi => Run(async token => new GalleryVM(await gi.FetchGalleryAsync())), 25, new GalleryInfoComparer());

        private class GalleryInfoComparer : IEqualityComparer<GalleryInfo>
        {
            public bool Equals(GalleryInfo x, GalleryInfo y)
            {
                return x.ID == y.ID;
            }

            public int GetHashCode(GalleryInfo obj)
            {
                return obj.ID.GetHashCode();
            }
        }

        public static GalleryVM GetVM(Gallery gallery)
        {
            var gi = new GalleryInfo(gallery.ID, gallery.Token);
            if (Cache.TryGet(gi, out var vm))
            {
                vm.Gallery = gallery;
                if (gallery.Count <= vm.currentIndex)
                    vm.currentIndex = -1;
            }
            else
            {
                vm = new GalleryVM(gallery);
                Cache.Add(gi, vm);
            }
            return vm;
        }

        public static IAsyncOperation<GalleryVM> GetVMAsync(long parameter)
        {
            return Cache.GetAsync(new GalleryInfo(parameter, 0));
        }

        public static IAsyncOperation<GalleryVM> GetVMAsync(GalleryInfo gInfo)
        {
            return Cache.GetAsync(gInfo);
        }

        public GalleryImage GetCurrent()
        {
            try
            {
                return this.Gallery[this.currentIndex];
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private GalleryVM()
        {
            this.Share.Tag = this;
            this.Save.Tag = this;
            this.OpenImage.Tag = this;
            this.LoadOriginal.Tag = this;
            this.ReloadImage.Tag = this;
            this.SearchUploader.Tag = this;
            this.SearchImage.Tag = this;
            this.AddComment.Tag = this;
            this.GoToLatestRevision.Tag = this;
        }

        private void Image_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GalleryImage.OriginalLoaded))
                this.LoadOriginal.OnCanExecuteChanged();
        }

        private GalleryVM(Gallery gallery)
            : this()
        {
            this.Gallery = gallery;
        }

        public Command<RevisionCollection> GoToLatestRevision { get; } = Command.Create<RevisionCollection>((sender, c) =>
        {
            var info = c.DescendantsInfo.Last().Gallery;
            var load = GetVMAsync(info);
            if (load.Status != AsyncStatus.Completed)
                RootControl.RootController.TrackAsyncAction(load, async (s, e) =>
                {
                    await DispatcherHelper.YieldIdle();
                    RootControl.RootController.Frame.Navigate(typeof(GalleryPage), info.ID);
                });
            else
                RootControl.RootController.Frame.Navigate(typeof(GalleryPage), info.ID);
        }, (sender, c) => c != null && c.DescendantsInfo.Count != 0);

        public Command<GalleryImage> Share { get; } = Command.Create<GalleryImage>(async (sender, image) =>
        {
            var that = (GalleryVM)sender.Tag;
            if (!ShareHandler.IsShareSupported)
            {
                if (image == null)
                    await Launcher.LaunchUriAsync(that.gallery.GalleryUri, new LauncherOptions { IgnoreAppUriHandlers = true });
                else
                    await Launcher.LaunchUriAsync(image.PageUri, new LauncherOptions { IgnoreAppUriHandlers = true });
                return;
            }
            var gallery = that.gallery;
            ShareHandler.Share(async (s, e) =>
            {
                var deferral = e.Request.GetDeferral();
                try
                {
                    var data = e.Request.Data;
                    data.Properties.Title = gallery.GetDisplayTitle();
                    data.Properties.Description = gallery.GetSecondaryTitle();
                    if (image == null)
                    {
                        data.Properties.ContentSourceWebLink = gallery.GalleryUri;
                        data.SetWebLink(gallery.GalleryUri);
                        data.SetText(gallery.GalleryUri.ToString());
                        if (gallery.Thumb != null)
                        {
                            var ms = new InMemoryRandomAccessStream();
                            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
                            encoder.SetSoftwareBitmap(gallery.Thumb);
                            await encoder.FlushAsync();
                            data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromStream(ms);
                            var firstImage = gallery.FirstOrDefault()?.ImageFile;
                            if (firstImage != null)
                                data.SetBitmap(RandomAccessStreamReference.CreateFromFile(firstImage));
                            else
                                data.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms));
                        }
                        if (gallery is SavedGallery)
                            while (gallery.HasMoreItems)
                                await gallery.LoadMoreItemsAsync(20);
                        var imageFiles = gallery
                            .Where(i => i.ImageFile != null)
                            .Select(i => new { i.ImageFile, Name = $"{i.PageID}{i.ImageFile.FileType}" })
                            .Where(f => f.ImageFile != null)
                            .ToList();
                        if (imageFiles.Count == 0)
                            return;
                        data.SetFolderProvider(imageFiles.Select(f => f.ImageFile), imageFiles.Select(f => f.Name), gallery.GetDisplayTitle());
                    }
                    else
                    {
                        data.Properties.ContentSourceWebLink = image.PageUri;
                        data.SetWebLink(image.PageUri);
                        data.SetText(image.PageUri.ToString());
                        var imageFile = image.ImageFile;
                        if (imageFile == null)
                            return;
                        var view = RandomAccessStreamReference.CreateFromFile(imageFile);
                        data.SetBitmap(view);
                        data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromStream(await imageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem));
                        var fileName = $"{image.PageID}{imageFile.FileType}";
                        data.SetFileProvider(imageFile, fileName);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }, (sender, image) => ((GalleryVM)sender.Tag).gallery != null);

        public Command Save { get; } = Command.Create(sender =>
        {
            var that = (GalleryVM)sender.Tag;
            that.SaveStatus = OperationState.Started;
            that.SaveProgress = -1;
            var task = that.gallery.SaveAsync(SettingCollection.Current.GetStrategy());
            task.Progress = (s, e) =>
            {
                that.SaveProgress = e.ImageLoaded * 100.0 / e.ImageCount;
            };
            task.Completed = (s, e) =>
            {
                switch (e)
                {
                case AsyncStatus.Canceled:
                case AsyncStatus.Error:
                    that.SaveStatus = OperationState.Failed;
                    RootControl.RootController.SendToast(s.ErrorCode, null);
                    break;
                case AsyncStatus.Completed:
                    that.SaveStatus = OperationState.Completed;
                    break;
                case AsyncStatus.Started:
                    that.SaveStatus = OperationState.Started;
                    break;
                }
                that.SaveProgress = 100;
            };
        }, sender =>
        {
            var that = (GalleryVM)sender.Tag;
            if (that.SaveStatus == OperationState.Started)
                return false;
            if (that.gallery is SavedGallery)
                return false;
            return true;
        });

        public Command<GalleryImage> OpenImage { get; } = Command.Create<GalleryImage>((sender, image) =>
        {
            var that = (GalleryVM)sender.Tag;
            that.CurrentIndex = image.PageID - 1;
            RootControl.RootController.Frame.Navigate(typeof(ImagePage), that.gallery.ID);
        }, (sender, image) => image != null);

        public Command<GalleryImage> LoadOriginal { get; } = Command.Create<GalleryImage>(async (sender, image) =>
        {
            var that = (GalleryVM)sender.Tag;
            image.PropertyChanged += that.Image_PropertyChanged;
            try
            {
                await image.LoadImageAsync(true, ConnectionStrategy.AllFull, true);
            }
            catch (Exception ex)
            {
                RootControl.RootController.SendToast(ex, typeof(ImagePage));
            }
            image.PropertyChanged -= that.Image_PropertyChanged;
        }, (sender, image) => image != null && !image.OriginalLoaded);

        public Command<GalleryImage> ReloadImage { get; } = Command.Create<GalleryImage>(async (sender, image) =>
        {
            var that = (GalleryVM)sender.Tag;
            image.PropertyChanged += that.Image_PropertyChanged;
            try
            {
                if (image.OriginalLoaded)
                    await image.LoadImageAsync(true, ConnectionStrategy.AllFull, true);
                else
                    await image.LoadImageAsync(true, SettingCollection.Current.GetStrategy(), true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RootControl.RootController.SendToast(ex, typeof(ImagePage));
            }
            image.PropertyChanged -= that.Image_PropertyChanged;
        }, (sender, image) => image != null);

        public Command<SHA1Value> SearchImage { get; } = Command.Create<SHA1Value>((sender, sha) =>
        {
            var that = (GalleryVM)sender.Tag;
            var search = Client.Current.Search("", Category.All, Enumerable.Repeat(sha, 1), that.gallery.GetDisplayTitle());
            var vm = SearchVM.GetVM(search);
            RootControl.RootController.Frame.Navigate(typeof(SearchPage), vm.SearchQuery.ToString());
        }, (sender, sha) => ((GalleryVM)sender.Tag).gallery != null && sha != default(SHA1Value));

        public Command SearchUploader { get; } = Command.Create(sender =>
        {
            var that = (GalleryVM)sender.Tag;
            var search = Client.Current.Search(that.gallery.Uploader, null, SettingCollection.Current.DefaultSearchCategory);
            var vm = SearchVM.GetVM(search);
            RootControl.RootController.Frame.Navigate(typeof(SearchPage), vm.SearchQuery.ToString());
        }, sender => ((GalleryVM)sender.Tag).gallery != null);

        private Gallery gallery;

        public Gallery Gallery
        {
            get => this.gallery;
            private set
            {
                if (this.gallery != null)
                    this.gallery.LoadMoreItemsException -= this.Gallery_LoadMoreItemsException;
                Set(ref this.gallery, value);
                if (this.gallery != null)
                    this.gallery.LoadMoreItemsException += this.Gallery_LoadMoreItemsException;
                this.Save.OnCanExecuteChanged();
                this.Share.OnCanExecuteChanged();
                this.AddComment.OnCanExecuteChanged();
                this.Torrents = null;
            }
        }

        private void Gallery_LoadMoreItemsException(IncrementalLoadingList<GalleryImage> sender, LoadMoreItemsExceptionEventArgs args)
        {
            RootControl.RootController.SendToast(args.Exception, typeof(GalleryPage));
            args.Handled = true;
        }

        private int currentIndex = -1;

        public int CurrentIndex
        {
            get => this.currentIndex;
            set => Set(ref this.currentIndex, value);
        }

        private string currentInfo;

        public string CurrentInfo
        {
            get => this.currentInfo;
            private set => Set(ref this.currentInfo, value);
        }

        public IAsyncAction RefreshInfoAsync()
        {
            return Run(async token =>
            {
                var current = GetCurrent();
                if (current?.ImageFile == null)
                {
                    this.CurrentInfo = Strings.Resources.Views.ImagePage.ImageFileInfoDefault;
                    return;
                }
                var prop = await current.ImageFile.GetBasicPropertiesAsync();
                var imageProp = await current.ImageFile.Properties.GetImagePropertiesAsync();
                this.CurrentInfo = string.Format(Strings.Resources.Views.ImagePage.ImageFileInfo, current.ImageFile.DisplayType,
                    Opportunity.Converters.Typed.ByteSizeToStringConverter.ByteSizeToString((long)prop.Size, Opportunity.Converters.Typed.UnitPrefix.Binary),
                    imageProp.Width.ToString(), imageProp.Height.ToString());
            });
        }

        private OperationState saveStatus;

        public OperationState SaveStatus
        {
            get => this.saveStatus;
            set
            {
                Set(ref this.saveStatus, value);
                this.Save.OnCanExecuteChanged();
            }
        }

        private double saveProgress;

        public double SaveProgress
        {
            get => this.saveProgress;
            set => Set(ref this.saveProgress, value);
        }

        #region Comments

        private static AddCommentDialog addComment;

        public AsyncCommand AddComment { get; } = AsyncCommand.Create(async (sender) =>
        {
            var that = (GalleryVM)sender.Tag;
            var addComment = System.Threading.LazyInitializer.EnsureInitialized(ref GalleryVM.addComment);
            addComment.Gallery = that.Gallery;
            await addComment.ShowAsync();
        }, sender => ((GalleryVM)sender.Tag).Gallery != null);

        public IAsyncAction LoadComments()
        {
            return Run(async token =>
            {
                try
                {
                    await this.gallery.Comments.FetchAsync();
                }
                catch (Exception ex)
                {
                    RootControl.RootController.SendToast(ex, typeof(GalleryPage));
                }
            });
        }

        #endregion Comments

        #region Torrents

        private static StorageFolder torrentfolder;

        private static IAsyncAction loadTorrentFolder()
        {
            return Run(async token =>
            {
                var ftoken = StatusCollection.Current.TorrentFolderToken;
                if (ftoken != null)
                {
                    try
                    {
                        torrentfolder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(ftoken);
                    }
                    catch (Exception)
                    {
                        //Load failed
                        torrentfolder = null;
                    }
                }
                if (torrentfolder == null)
                {
                    torrentfolder = await DownloadsFolder.CreateFolderAsync("Torrents", CreationCollisionOption.GenerateUniqueName);
                    if (ftoken == null)
                        ftoken = StorageApplicationPermissions.FutureAccessList.Add(torrentfolder);
                    else
                        StorageApplicationPermissions.FutureAccessList.AddOrReplace(ftoken, torrentfolder);
                }
                StatusCollection.Current.TorrentFolderToken = ftoken;
            });
        }

        public Command<TorrentInfo> TorrentDownload { get; } = Command.Create<TorrentInfo>(async (sender, torrent) =>
        {
            RootControl.RootController.SendToast(Strings.Resources.Views.GalleryPage.TorrentDownloading, null);
            try
            {
                var file = await torrent.LoadTorrentAsync();
                if (torrentfolder == null)
                    await loadTorrentFolder();
                await file.MoveAsync(torrentfolder, file.Name, NameCollisionOption.GenerateUniqueName);
                if (!await Launcher.LaunchFileAsync(file))
                    await Launcher.LaunchFolderAsync(torrentfolder);
                RootControl.RootController.SendToast(string.Format(Strings.Resources.Views.GalleryPage.TorrentDownloaded, torrentfolder.Path), null);
            }
            catch (Exception ex)
            {
                RootControl.RootController.SendToast(ex, typeof(GalleryPage));
            }
        }, (sender, torrent) => torrent != null && torrent.TorrentUri != null);

        public IAsyncAction LoadTorrents()
        {
            return Run(async token =>
            {
                try
                {
                    this.Torrents = await this.gallery.FetchTorrnetsAsync();
                }
                catch (Exception ex)
                {
                    RootControl.RootController.SendToast(ex, typeof(GalleryPage));
                }
            });
        }

        private ReadOnlyCollection<TorrentInfo> torrents;

        public ReadOnlyCollection<TorrentInfo> Torrents
        {
            get => this.torrents;
            private set
            {
                this.torrents = value;
                OnPropertyChanged(nameof(Torrents), nameof(TorrentCount));
            }
        }

        public int? TorrentCount => this.torrents?.Count ?? (this.gallery is SavedGallery ? null : this.gallery?.TorrentCount);

        #endregion Torrents
    }
}
