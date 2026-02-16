using System;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;
using Rhino.Render;
using System.Net;
using Newtonsoft.Json.Linq;

namespace MOTH_2
{
    public class MOTH_Component2 : GH_Component
    {
        // Cache the last geocoded result to avoid repeated API calls
        private string _cachedAddress = "";
        private double _cachedLat = 0;
        private double _cachedLng = 0;

        public MOTH_Component2()
          : base("Solar Rotation", "SolarRot",
            "Rotates object based on sun position using a site address",
            "MyTools", "Solar")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("Geometry", "G", "Geometry to rotate", GH_ParamAccess.item);
            pManager.AddTextParameter("Address", "A", "Site address or place name (e.g. London, UK)", GH_ParamAccess.item, "London, UK");
            pManager.AddBooleanParameter("Fetch", "F", "Set to True to geocode the address", GH_ParamAccess.item, false);
            pManager.AddIntegerParameter("Year", "Y", "Year", GH_ParamAccess.item, 2024);
            pManager.AddIntegerParameter("Month", "M", "Month (1-12)", GH_ParamAccess.item, 6);
            pManager.AddIntegerParameter("Day", "D", "Day (1-31)", GH_ParamAccess.item, 21);
            pManager.AddNumberParameter("Time", "T", "Hour (0-23.99)", GH_ParamAccess.item, 12.0);
            pManager.AddPointParameter("Center", "C", "Rotation center point", GH_ParamAccess.item, Point3d.Origin);
            pManager.AddBooleanParameter("UseAltitude", "Alt", "Include altitude angle in rotation", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Rotated Geometry", "RG", "Geometry rotated to face sun", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "X", "Transformation matrix", GH_ParamAccess.item);
            pManager.AddNumberParameter("Azimuth", "Az", "Sun azimuth angle (degrees)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Altitude", "Alt", "Sun altitude angle (degrees)", GH_ParamAccess.item);
            pManager.AddVectorParameter("SunVector", "V", "Direction vector to sun", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "Lat", "Resolved latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "Lng", "Resolved longitude", GH_ParamAccess.item);
            pManager.AddTextParameter("Info", "I", "Status and sun position information", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Input variables
            GeometryBase geometry = null;
            string address = "London, UK";
            bool fetch = false;
            int year = 0;
            int month = 0;
            int day = 0;
            double time = 0;
            Point3d center = Point3d.Origin;
            bool useAltitude = false;

            // Get data
            if (!DA.GetData(0, ref geometry)) return;
            if (!DA.GetData(1, ref address)) return;
            if (!DA.GetData(2, ref fetch)) return;
            if (!DA.GetData(3, ref year)) return;
            if (!DA.GetData(4, ref month)) return;
            if (!DA.GetData(5, ref day)) return;
            if (!DA.GetData(6, ref time)) return;
            if (!DA.GetData(7, ref center)) return;
            if (!DA.GetData(8, ref useAltitude)) return;

            // Validate date/time inputs
            if (month < 1 || month > 12)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Month must be between 1 and 12");
                return;
            }
            if (day < 1 || day > 31)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Day must be between 1 and 31");
                return;
            }
            if (time < 0 || time >= 24)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Time must be between 0 and 23.99");
                return;
            }

            double lat = _cachedLat;
            double lng = _cachedLng;

            // Only geocode when Fetch is true
            if (fetch)
            {
                // Only call API if address has changed
                if (address != _cachedAddress)
                {
                    try
                    {
                        // First try parsing as raw coordinates e.g. "51.5074, -0.1278"
                        if (TryParseCoordinates(address, out lat, out lng))
                        {
                            _cachedAddress = address;
                            _cachedLat = lat;
                            _cachedLng = lng;
                        }
                        else
                        {
                            // Use synchronous WebClient to avoid thread deadlock
                            GeocodeAddress(address, out lat, out lng);
                            _cachedAddress = address;
                            _cachedLat = lat;
                            _cachedLng = lng;
                        }
                    }
                    catch (Exception ex)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Geocoding failed: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    // Use cached values
                    lat = _cachedLat;
                    lng = _cachedLng;
                }
            }
            else
            {
                // Fetch is false
                if (_cachedAddress == "")
                {
                    DA.SetData(7, "Enter an address and set Fetch to True");
                    return;
                }
                // Use last cached coordinates
                lat = _cachedLat;
                lng = _cachedLng;
            }

            try
            {
                // Convert decimal time to hours minutes seconds
                int hour = (int)time;
                int minute = (int)((time - hour) * 60);
                int second = (int)((((time - hour) * 60) - minute) * 60);

                // Create DateTime
                DateTime dateTime = new DateTime(year, month, day, hour, minute, second);

                // Calculate sun position using Rhino's built-in Sun
                var sun = new Rhino.Render.Sun();
                sun.SetPosition(dateTime, lat, lng);

                double azimuth = sun.Azimuth;
                double altitude = sun.Altitude;

                // Build sun direction vector
                double azimuthRad = RhinoMath.ToRadians(azimuth);
                double altitudeRad = RhinoMath.ToRadians(altitude);

                Vector3d sunVector = new Vector3d(
                    Math.Cos(altitudeRad) * Math.Sin(azimuthRad),
                    Math.Cos(altitudeRad) * Math.Cos(azimuthRad),
                    Math.Sin(altitudeRad)
                );

                // Create rotation transform
                Transform rotation;

                if (useAltitude)
                {
                    Vector3d fromVector = Vector3d.YAxis;
                    rotation = Transform.Rotation(fromVector, sunVector, center);
                }
                else
                {
                    rotation = Transform.Rotation(azimuthRad, Vector3d.ZAxis, center);
                }

                // Apply transformation to geometry
                GeometryBase rotatedGeometry = geometry.Duplicate();
                rotatedGeometry.Transform(rotation);

                // Outputs
                DA.SetData(0, rotatedGeometry);
                DA.SetData(1, rotation);
                DA.SetData(2, azimuth);
                DA.SetData(3, altitude);
                DA.SetData(4, sunVector);
                DA.SetData(5, lat);
                DA.SetData(6, lng);

                string info = $"Address: {_cachedAddress}\n" +
                             $"Location: {lat:F4}, {lng:F4}\n" +
                             $"Date/Time: {dateTime:yyyy-MM-dd HH:mm}\n" +
                             $"Azimuth: {azimuth:F2} degrees from North\n" +
                             $"Altitude: {altitude:F2} degrees above horizon";

                if (altitude < 0)
                {
                    info += "\nWarning: Sun is below the horizon at this time";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Sun is below horizon");
                }

                DA.SetData(7, info);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        // Synchronous geocoding using WebClient - avoids thread deadlock in Grasshopper
        private void GeocodeAddress(string address, out double lat, out double lng)
        {
            lat = lng = 0;

            string url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&format=json&limit=1";

            using (WebClient client = new WebClient())
            {
                // Nominatim requires a User-Agent header
                client.Headers.Add("User-Agent", "MOTH_GrasshopperPlugin/1.0");
                client.Headers.Add("Accept-Language", "en");

                string response = client.DownloadString(url);
                JArray json = JArray.Parse(response);

                if (json.Count == 0)
                    throw new Exception($"No results found for: '{address}'. Try a more specific address.");

                lat = double.Parse((string)json[0]["lat"],
                    System.Globalization.CultureInfo.InvariantCulture);
                lng = double.Parse((string)json[0]["lon"],
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // Try to parse "lat, lng" string directly
        private bool TryParseCoordinates(string input, out double lat, out double lng)
        {
            lat = lng = 0;
            var parts = input.Split(',');
            if (parts.Length == 2)
            {
                return double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out lat) &&
                       double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out lng);
            }
            return false;
        }

        protected override System.Drawing.Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("a1234567-89ab-cdef-0123-456789abcdef");
    }
}