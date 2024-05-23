using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using SnipeSharp;
using SnipeSharp.Endpoints.Models;
using SnipeSharp.Endpoints.SearchFilters;
using SnipeSharp.Common;
using System.Net;
using Newtonsoft.Json;
using SnipeSharp.Endpoints;
using SnipeSharp.Endpoints.ExtendedManagers;

namespace SnipeAgent
{
    class Broker
    {
        public Broker()
        {

        }

        public bool IsIdentical(Asset a1, Asset a2)
        {
            // We only need to check fields that are being populated by the SnipeAgent agent
            // i.e.. not ID, since that is managed by DB

            if (a1.AssetTag != a2.AssetTag || a1.Serial != a2.Serial || a1.Name != a2.Name)
            {
                return false;
            }

            if (a1.WarrantyMonths != a2.WarrantyMonths)
            {
                return false;
            }

            if (a1.Location?.Id != a2.Location?.Id)
            {
                return false;
            }

            // Checking sub-object IDs
            if (a1.Company?.Id != a2.Company?.Id || a1.Model?.Id != a2.Model?.Id ||
                a1.StatusLabel?.Id != a2.StatusLabel?.Id)
            {
                return false;
            }

            // Should be something for custom fields -> for now leaving blank

            return true;
        }

        public bool CheckConnection(NameValueCollection appSettings)
        {
            // This method might seem overly complicated for what it is doing (simply
            // checking a connection to the Snipe-IT instance. However, there are a lot
            // of different ways that the connection can fail (usually related to improperly
            // set values in the config file).

            // This method allows a set of specific, descriptive error messages to be passed
            // showing exactly what kind of configuration problem needs to be fixed.
            
            string uri = "";
            string query = "users?limit=0";
            string baseUri = appSettings["BaseURI"];

            // Note: The program should be able to handle a BaseURI that has a trailing '/' or not.

            if (baseUri.EndsWith("/")){
                uri = baseUri + query; 
            } else
            {
                uri = baseUri + "/" + query;
            }

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(uri);
                request.Headers["Authorization"] = "Bearer " + appSettings["API"];
                request.Accept = "application/json";
            }
            catch (System.NotSupportedException e)
            {
                Console.WriteLine(e);
                Console.WriteLine("Please double-check the BaseURI key in your <appSettings>\nblock of the SnipeAgent config file and ensure it points to your instance of Snipe-IT.");
                return false;
            }
            try
            {
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Console.WriteLine("HTTP 200: Connection to Snipe-IT instance succeded.");
                    return true;
                } else {
                    Console.WriteLine("HTTP {0}", response.StatusCode);
                    Console.WriteLine("Unexpected HTTP response code, could not connect to Snipe-IT instance.");
                    return false;
                }
            } catch (WebException e)
            {
                HttpWebResponse r = (HttpWebResponse)e.Response;
                if (r == null)
                {
                    Console.WriteLine(e);
                    Console.WriteLine("Please double-check the BaseURI key in your <appSettings>\nblock of the SnipeAgent config file and ensure it points to your instance of Snipe-IT.");
                }
                else if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Console.WriteLine("HTTP 403: Unauthorized. Please check the API key value in your <appSettings>\nblock of the SnipeAgent config file and ensure it has been set to a valid key.");
                }
                else if (r.StatusCode == HttpStatusCode.NotFound)
                {
                    Console.WriteLine("HTTP 404: URL not found. Please double-check the BaseURI key in your <appSettings>\nblock of the SnipeAgent config file and ensure it points to your instance of Snipe-IT.");
                } else
                {
                    Console.WriteLine("Unexpected error, could not connect to Snipe-IT instance.");
                    Console.WriteLine(e);
                }
                return false;
            }
        }

        public List<IRequestResponse> SyncAll(SnipeItApi snipe, Asset currentAsset, Model currentModel, Manufacturer currentManufacturer,
            Category currentCategory, Company currentCompany, StatusLabel currentStatusLabel, Location currentLocation)
        {
            
            // Let's try to simplify the logic into a repeatable structure:

            // Each of these categories (Asset, Model, Location, etc.) has a single
            // value that uniquely specifies it, which must be:
            // a) Uniquely associated to it in the physical world
            // b) Not the database unique ID

            // Why can't it be the database unique ID? Because the uniqueID is not something that 
            // is inherently associated to the computer's hardware, it's just a number that SnipeIT
            // uses internally.

            // By category: Unique identifiers

            // Asset: computer serial (string)
            // Model: model full name (string)
            // Category: model types, as given by the WMIC fullnames (string)
            // Manufacturer: full name (string)
            // Company: full name (string)
            // StatusLabel: full name (string)
            // Location: full name (string)

            // Really, we only need the update functionality for the assets, as the computer name can change,
            // along with its location, etc.

            // However we are not in change of things like a computer manufacturer changing the name of
            // its company. So those have a simpler functionality.


            List<IRequestResponse> messages = new List<IRequestResponse>();

            SearchFilter manufacturerFilter = new SearchFilter { Search = currentManufacturer.Name };
            var updatedManufacturer = CreateEntityIfDoesNotExists(snipe.ManufacturerManager, currentManufacturer, manufacturerFilter, messages);

            SearchFilter categoryFilter = new SearchFilter { Search = currentCategory.Name };
            var updatedCategory = CreateEntityIfDoesNotExists(snipe.CategoryManager, currentCategory, categoryFilter, messages);

            currentModel.Manufacturer = updatedManufacturer;
            currentModel.Category = updatedCategory;

            SearchFilter modelFilter = new SearchFilter { Search = currentModel.Name };
            var updatedModel = CreateEntityIfDoesNotExists(snipe.ModelManager, currentModel, modelFilter, messages);

            if (updatedModel.Manufacturer.Id != updatedManufacturer.Id || updatedModel.Category.Id != updatedCategory.Id)
            {
                // Model has changed
                var message = snipe.ModelManager.Create(currentModel);
                if (message.Status != "success")
                    throw new Exception("Cannot create model.");
                
                messages.Add(message);
                updatedModel = snipe.ModelManager.Get((int)message.Payload.Id);
            }

            SearchFilter companyFilter = new SearchFilter { Search = currentCompany.Name };
            Company updatedCompany = CreateEntityIfDoesNotExists(snipe.CompanyManager, currentCompany, companyFilter, messages);

            SearchFilter statusLabelFilter = new SearchFilter { Search = currentStatusLabel.Name };
            StatusLabel updatedStatusLabel = CreateEntityIfDoesNotExists(snipe.StatusLabelManager, currentStatusLabel, statusLabelFilter, messages);

            SearchFilter locationFilter = new SearchFilter { Search = currentLocation.Name };
            Location updatedLocation = CreateEntityIfDoesNotExists(snipe.LocationManager, currentLocation, locationFilter, messages);

            currentAsset.Model = updatedModel;
            currentAsset.Company = updatedCompany;
            currentAsset.StatusLabel = updatedStatusLabel;
            currentAsset.Location = updatedLocation;

            string currentSerial = currentAsset.Serial;

            Asset dbAsset = FindAssetBySerial(snipe.AssetManager, currentSerial); 

            if (dbAsset == null)
            {
                Console.WriteLine("Asset does not exist in database, creating...");
                snipe.AssetManager.Create(currentAsset);
            } else
            {
                Console.WriteLine("Asset already exists in db. Checking for consistency.");
                bool isIdentical = IsIdentical(currentAsset, dbAsset);
                if (isIdentical)
                {
                    Console.WriteLine("No changes required! Asset already exists and is up-to-date.");
                } else
                {
                    Console.WriteLine("Changes in asset detected. Updating:");

                    // Setting old ID for consistency
                    currentAsset.Id = dbAsset.Id;
                    currentAsset.LastCheckout = dbAsset.LastCheckout;
                    currentAsset.AssignedTo = dbAsset.AssignedTo;
                    messages.Add(snipe.AssetManager.Update(currentAsset));

                }
            }
            
            return messages;
        }

        private static T CreateEntityIfDoesNotExists<T>(IEndpointManager<T> endpointManager, T currentEntity, SearchFilter entityFilter, List<IRequestResponse> messages) 
            where T : ICommonEndpointModel
        {
            var entity = endpointManager.FindOne(entityFilter);
            if (entity != null)
                return entity;

            messages.Add(endpointManager.Create(currentEntity));
            entity = endpointManager.FindOne(entityFilter);

            return entity;
        }

        private Asset FindAssetBySerial(AssetEndpointManager assetManager, string serial)
        {
            string response = assetManager.ReqManager.Get($"{assetManager.EndPoint}/byserial/{serial}");
            var result = JsonConvert.DeserializeObject<ResponseCollection<Asset>>(response);

            return result.Total == 0L ? null : result.Rows?[0];
        }
    }
}
