using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using GoogleApi;
using GoogleApi.Entities.Common;
using GoogleApi.Entities.Common.Enums;
using GoogleApi.Entities.Places.Details.Request;
using GoogleApi.Entities.Places.Photos.Request;
using GoogleApi.Entities.Places.Search.NearBy.Request;
using Airport.Models;
using MaxMind.GeoIP2;

namespace Airport.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IHostingEnvironment _hostingEnvironment;
        public string MapboxAccessToken { get; }
        public string GoogleApiKey { get; }

        public double InitialLatitude { get; set; } = 0;
        public double InitialLongitude { get; set; } = 0;
        public int InitialZoom { get; set; } = 1;

        public IndexModel(IConfiguration configuration, IHostingEnvironment hostingEnvironment)
        {
            _hostingEnvironment = hostingEnvironment;

            MapboxAccessToken = configuration["Mapbox:AccessToken"];
            GoogleApiKey = configuration["google:ApiKey"];
        }
        
        public IActionResult OnGetAirports()
        {
            var configuration = new Configuration
            {
                BadDataFound = context => { }
            };

            using (var sr = new StreamReader(Path.Combine(_hostingEnvironment.WebRootPath, "airports.dat.txt")))
            using (var reader = new CsvReader(sr, configuration))
            {
                FeatureCollection featureCollection = new FeatureCollection();

                while (reader.Read())
                {
                    string name = reader.GetField<string>(1);
                    string iataCode = reader.GetField<string>(4);
                    string _latitude = reader.GetField<string>(6);
                    string _longitude = reader.GetField<string>(7);
                    _latitude = _latitude.Replace(",", ".").Replace(" ", "").Replace("\"", "").Replace("'", "");
                    _longitude= _longitude.Replace(",", ".").Replace(" ", "").Replace("\"", "").Replace("'", "");
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
                    double latitude = double.Parse(_latitude);
                    double longitude = double.Parse(_longitude);
                    featureCollection.Features.Add(new Feature(
                        new Point(new Position(latitude, longitude)),
                        new Dictionary<string, object>
                        {
                    {"name", name},
                    {"iataCode", iataCode}
                        }));
                }

                return new JsonResult(featureCollection);
            }
        }
        public void OnGet()
        {
            //Получить местоположение
            // Затем мы напишем некоторый код, который откроет DatabaseReaderэкземпляр, 
            //а затем вызовет City()метод, передав IP-адрес, который вы хотите найти. 
            //    В приложении ASP.NET Core вы можете определить IP-адрес, используя HttpContext.Connection.RemoteIpAddress.
            // Если мы сможем найти местоположение, мы сохраним его InitialLatitudeи InitialLongitudeполя, 
            //а также установите начальный масштаб, который будет увеличен в этом месте.
            //Если по какой - то причине взгляд не сработает, мы просто потерпим неудачу.
            //Нам нужно будет добавить X - Forwarded - For заголовок к нашим запросам.
            //    Я использую Firefox и установил расширение Modify Header Value.
            //    Если вы используете Chrome, вы можете установить расширение ModHeader, 
            //    которое позволит вам изменять HTTP-заголовки запросов.
            //    Нам понадобится IP - адрес для тестирования. Самый простой способ - напечатать 
            //    «что такое мой IP-адрес» в вашей поисковой системе, и он вам скажет.Я использую DuckDuckGo, но Google сделает то же самое для вас:
            try
            {
                using (var reader = new DatabaseReader(_hostingEnvironment.WebRootPath + "\\GeoLite2-City.mmdb"))
                {
                    // Определение IP-адреса запроса
                    var ipAddress = HttpContext.Connection.RemoteIpAddress;
                    // Получить город по IP-адресу
                    var city = reader.City(ipAddress);

                    if (city?.Location?.Latitude != null && city?.Location?.Longitude != null)
                    {
                        InitialLatitude = city.Location.Latitude.Value;
                        InitialLongitude = city.Location.Longitude.Value;
                        InitialZoom = 9;
                    }
                }
            }
            catch (Exception e)
            {
                // Просто подавите ошибки. Если мы не смогли восстановить местоположение по какой-либо причине
                // нет причин уведомлять пользователя. Мы просто не будем знать их текущий
                // расположение и не сможет центрировать карту на нем
            }
        }
        

        public async Task<IActionResult> OnGetAirportDetail(string name, double latitude, double longitude)
        {
            var airportDetail = new AirportDetail();

            // Выполнить поисковый запрос
            var searchResponse = await GooglePlaces.NearBySearch.QueryAsync(new PlacesNearBySearchRequest
            {
                Key = GoogleApiKey,
                Name = name,
                Location = new Location(latitude, longitude),
                Radius = 1000
            });

            // Если мы не получили положительный ответ, или список результатов пуст, то уходим отсюда
            if (!searchResponse.Status.HasValue || searchResponse.Status.Value != Status.Ok || !searchResponse.Results.Any())
                return new BadRequestResult();

            // Получить первый результат
            var nearbyResult = searchResponse.Results.FirstOrDefault();
            string placeId = nearbyResult.PlaceId;
            string photoReference = nearbyResult.Photos?.FirstOrDefault()?.PhotoReference;
            string photoCredit = nearbyResult.Photos?.FirstOrDefault()?.HtmlAttributions.FirstOrDefault();

            // Выполнить запрос сведений
            var detailsResonse = await GooglePlaces.Details.QueryAsync(new PlacesDetailsRequest
            {
                Key = GoogleApiKey,
                PlaceId = placeId
            });

            // Если мы не получили положительный ответ, или список результатов пуст, то уходим отсюда
            if (!detailsResonse.Status.HasValue || detailsResonse.Status.Value != Status.Ok)
                return new BadRequestResult();

            // Установливаем детали
            var detailsResult = detailsResonse.Result;
            airportDetail.FormattedAddress = detailsResult.FormattedAddress;
            airportDetail.PhoneNumber = detailsResult.InternationalPhoneNumber;
            airportDetail.Website = detailsResult.Website;

            if (photoReference != null)
            {
                // Выполняем запрос к фотографии
                var photosResponse = await GooglePlaces.Photos.QueryAsync(new PlacesPhotosRequest
                {
                    Key = GoogleApiKey,
                    PhotoReference = photoReference,
                    MaxWidth = 400
                });
                
                if (photosResponse.Buffer != null)
                {
                    airportDetail.Photo = Convert.ToBase64String(photosResponse.Buffer);
                    airportDetail.PhotoCredit = photoCredit;
                }
            }

            return new JsonResult(airportDetail);
        }
    }
}
