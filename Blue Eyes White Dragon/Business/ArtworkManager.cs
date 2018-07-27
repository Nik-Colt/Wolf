﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blue_Eyes_White_Dragon.Business.Interface;
using Blue_Eyes_White_Dragon.Business.Models;
using Blue_Eyes_White_Dragon.DataAccess.Interface;
using Blue_Eyes_White_Dragon.Misc;
using Blue_Eyes_White_Dragon.Misc.Interface;
using Blue_Eyes_White_Dragon.UI.Models;
using PhotoSauce.MagicScaler;

namespace Blue_Eyes_White_Dragon.Business
{
    public class ArtworkManager : IArtworkManager
    {
        private readonly IFileRepository _fileRepo;
        private readonly ICardRepository _cardRepo;
        private readonly ILogger _logger;
        private readonly IResourceRepository _resourceRepo;
        private readonly ISettingRepository _settingRepo;
        private readonly FileInfo _errorImage;
        private readonly IImageRepository _imageRepo;

        public ArtworkManager(IFileRepository fileRepo, ICardRepository cardRepo, ILogger logger, 
            IResourceRepository resourceRepo, ISettingRepository settingRepo,
            IImageRepository imageRepo
        )
        {
            _fileRepo = fileRepo ?? throw new ArgumentNullException(nameof(fileRepo));
            _cardRepo = cardRepo ?? throw new ArgumentNullException(nameof(cardRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resourceRepo = resourceRepo;
            _settingRepo = settingRepo;
            _errorImage = _resourceRepo.LoadErrorImageFromResource();
            _imageRepo = imageRepo;
        }

        public List<Artwork> CreateArtworkModels(List<Card> gameCards, DirectoryInfo gameImagesLocation, DirectoryInfo replacementImagesLocation)
        {
            var artworkList = new ConcurrentBag<Artwork>();

            Parallel.For(0, gameCards.Count, i =>
            {
                var gameCard = gameCards[i];
                var gameImageFile = SearchForImage(gameCard.Id, gameImagesLocation);

                artworkList.Add(new Artwork()
                {
                    CardId = gameCard.Id,
                    GameImageFile = gameImageFile ?? _errorImage,
                    GameImageMonsterName = gameCard.Name,
                    IsMatched = false,
                    IsPendulum = gameCard.IsPendulum
                });
            });

            //foreach (var artwork in artworkList)
            //{
            //    var gameCard = gameCards.Find(x => x.Id == artwork.CardId);
            //    artwork.GameImageFile = SearchForImage(gameCard.Id, gameImagesLocation) ?? _errorImage;
            //}

            return artworkList.ToList();
        }

        public List<Artwork> UpdateArtworkModelsWithReplacement(IEnumerable<Artwork> artworkList, bool useIncludedPendulum)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var artworks = artworkList.ToList();
            var numberOfArtwork = artworks.Count;
            long progress = 0;

            foreach (var artwork in artworks)
            {
                if (useIncludedPendulum && artwork.IsPendulum)
                {
                    ProcessArtworkAsPendulum(artwork);
                }
                else
                {
                    ProcessArtwork(artwork);
                }
                progress++;
                _logger.LogInformation(Localization.InformationProcessingProgress(progress, numberOfArtwork, artwork.GameImageMonsterName));
            }
            stopwatch.Stop();
            _logger.LogInformation(Localization.InformationProcessingDone(artworks.Count, MiliToSec(stopwatch.ElapsedMilliseconds)));

            return artworks;
        }

        private void ProcessArtworkAsPendulum(Artwork artwork)
        {
            var pendulumLocation = _resourceRepo.GetPendulumPathFromResource();
            var currentPendulumImage = SearchForImage(artwork.CardId, pendulumLocation);

            if (currentPendulumImage != null)
            {
                artwork.ReplacementImageFile = currentPendulumImage;
                artwork.IsMatched = true;
            }
            else
            {
                artwork.ReplacementImageFile = _errorImage;
                artwork.IsMatched = false;
            }
            artwork.ReplacementImageMonsterName = artwork.GameImageMonsterName;
        }

        private void ProcessArtwork(Artwork artwork)
        {
            var replacementCard = FindSuitableReplacementCard(artwork);
            artwork.ReplacementImageMonsterName = replacementCard.GameImageMonsterName;
            artwork.ReplacementImageFile = replacementCard.ReplacementImageFile;
        }

        private Artwork FindSuitableReplacementCard(Artwork artwork)
        {
            var matchingCards = SearchCards(artwork);
            var replacementCard = matchingCards.FirstOrDefault();

            if (replacementCard == null)
            {
                HandleNoMatch(artwork);
            }

            if (matchingCards.Count == 1)
            {
                HandleSingleMatch(replacementCard, artwork);
            }

            if (matchingCards.Count > 1)
            {
                HandleMultipleMatches(matchingCards, artwork);
            }

            return artwork;
        }

        private List<Card> SearchCards(Artwork artwork)
        {
            try
            {
                return _cardRepo.GetCards(artwork.GameImageMonsterName);
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Database error: {e}, inner: {e.InnerException} for {artwork.GameImageMonsterName}");
                return new List<Card>();
            }
        }

        private void HandleSingleMatch(Card replacementCard, Artwork artwork)
        {
            var replacementImagesDir = new DirectoryInfo(_settingRepo.GetPathSetting(Constants.Setting.LastUsedReplacementImagePath));

            var imageFile = SearchForImage(replacementCard.Id, replacementImagesDir);
            if (imageFile != null)
            {
                artwork.ReplacementImageFile = imageFile;
                artwork.IsMatched = true;
            }
            else
            {
                artwork.ReplacementImageFile = _errorImage;
                artwork.IsMatched = false;
            }
        }

        private void HandleMultipleMatches(ICollection<Card> matchingCards, Artwork artwork)
        {
            var replacementImagesDir = new DirectoryInfo(_settingRepo.GetPathSetting(Constants.Setting.LastUsedReplacementImagePath));

            var firstCard = matchingCards.First();
            matchingCards.Remove(firstCard);
            HandleSingleMatch(firstCard, artwork);

            foreach (var card in matchingCards)
            {
                var imageFile = SearchForImage(card.Id, replacementImagesDir);
                if (imageFile == null)
                {
                    imageFile = _errorImage;
                    artwork.IsMatched = false;
                }
                artwork.AlternateReplacementImages.Add(imageFile);
            }
        }

        private void HandleNoMatch(Artwork artwork)
        {
            artwork.ReplacementImageMonsterName = artwork.GameImageMonsterName;
            artwork.ReplacementImageFile = _errorImage;
            artwork.IsMatched = false;
            _logger.LogInformation(Localization.ErrorNoMatch(artwork.GameImageMonsterName));
        }

        private long MiliToSec(long stopwatchElapsedMilliseconds)
        {
            return stopwatchElapsedMilliseconds / 1000;
        }

        public FileInfo SearchForImage(int cardId, DirectoryInfo directory)
        {
            FileInfo imageFile = null;

            var images = SearchForImagesInDirectory(cardId, directory);

            if (images.Count > 1)
            {
                imageFile = images.FirstOrDefault(x => x.Extension == Constants.SupportedImageType.jpg.ToString());
                _logger.LogInformation(Localization.InformationMultipleImagesFound(images.Count, cardId, imageFile?.Name));
                //TODO what to do when a jpg and a png of the card exists in the folder?
            }
            else if (images.Count == 0)
            {
                _logger.LogInformation(Localization.ErrorNoImageFound(cardId));
                return null;
            }
            else
            {
                imageFile = images.First();
            }
            return imageFile;
        }

        public void ConvertAll(IEnumerable<Artwork> artworks)
        {
            var artworkList = artworks.ToList();
            var destinationPath = _resourceRepo.GetOutputPath();
            long progress = 0;
            var numberOfArtwork = artworkList.ToList().Count();

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            _logger.LogInformation(Localization.InformationConvertingImages);

            var settings = new ProcessImageSettings
            {
                SaveFormat = FileFormat.Jpeg,
                JpegQuality = 92,
            };

            foreach (var artwork in artworkList)
            {
                var imageFile = artwork.ReplacementImageFile;
                if (artwork.IsPendulum)
                {
                    ConvertPendulumArtwork(destinationPath, artwork.GameImageFileName, imageFile, settings);
                }
                else
                {
                    ConvertNormalArtwork(destinationPath, artwork.GameImageFileName, imageFile, settings);
                }
                _logger.LogInformation(Localization.InformationProcessingProgress(progress, numberOfArtwork, artwork.GameImageMonsterName));
                progress++;
            }

            stopwatch.Stop();
            _logger.LogInformation(Localization.InformationProcessingDone(numberOfArtwork, MiliToSec(stopwatch.ElapsedMilliseconds)));

        }

        private void ConvertNormalArtwork(DirectoryInfo destinationPath, string orgName, FileInfo imageFile,
            ProcessImageSettings settings)
        {
            settings.Width = 304;
            settings.Height = 304;
            settings.JpegSubsampleMode = ChromaSubsampleMode.Subsample420;
            _imageRepo.ConvertImage(destinationPath, imageFile, orgName, settings);
        }

        private void ConvertPendulumArtwork(DirectoryInfo destinationPath, string orgName, FileInfo imageFile,
            ProcessImageSettings settings)
        {
            settings.Width = 347;
            settings.Height = 444;
            settings.JpegSubsampleMode = ChromaSubsampleMode.Default;
            _imageRepo.ConvertImage(destinationPath, imageFile, orgName, settings);
        }

        private List<FileInfo> SearchForImagesInDirectory(int cardId, DirectoryInfo directory)
        {
            List<FileInfo> foundImages = new List<FileInfo>();

            var filetypes = _fileRepo.GetSupportedFileTypes();

            foreach (var supportedImageType in filetypes)
            {
                var filename = Path.ChangeExtension(cardId.ToString(), supportedImageType);
                var image = _fileRepo.GetImageFile(filename, directory);
                if (image != null)
                {
                    foundImages.Add(image);
                }
            }

            return foundImages;
        }
    }
}
