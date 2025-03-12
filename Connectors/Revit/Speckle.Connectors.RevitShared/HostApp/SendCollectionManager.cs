using System.IO;
using Autodesk.Revit.DB;
using Speckle.Converters.Common;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk;
using Speckle.Sdk.Models.Collections;

namespace Speckle.Connectors.Revit.HostApp;

/// <summary>
/// Manages efficiently creating host collections for revit elements on send. Expects to be scoped per send operation.
/// </summary>
public class SendCollectionManager
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _converterSettings;
  private readonly Dictionary<string, Collection> _collectionCache = new();
  private readonly Dictionary<ElementId, (string name, Dictionary<string, object?> props)> _levelCache = new(); // stores level id and its properties

  public SendCollectionManager(IConverterSettingsStore<RevitConversionSettings> converterSettings)
  {
    _converterSettings = converterSettings;
  }

  /// <summary>
  /// Returns the element's host collection based on level, category and optional type. Additionally, places the host collection on the provided root object.
  /// Note, it's not nice we're mutating the root object in this function.
  /// </summary>
  /// <param name="element"></param>
  /// <param name="rootObject"></param>
  /// <returns></returns>
  public Collection GetAndCreateObjectHostCollection(Element element, Collection rootObject)
  {
    var doc = _converterSettings.Current.Document;
    var path = new List<string>();

    // get model name (filename without extension) as the first level in the hierarchy
    string fileName = Path.GetFileNameWithoutExtension(doc.PathName);
    path.Add(fileName);

    // Step 0: get the level and its properties
    string levelName = "No Level";
    Dictionary<string, object?> levelProperties = new();
    if (element.LevelId != ElementId.InvalidElementId)
    {
      if (_levelCache.TryGetValue(element.LevelId, out var cachedLevel))
      {
        levelName = cachedLevel.name;
        levelProperties = cachedLevel.props;
      }
      else
      {
        try
        {
          var level = (Level)doc.GetElement(element.LevelId);
          levelName = level.Name;
          levelProperties.Add("elevation", level.Elevation);
          levelProperties.Add("units", _converterSettings.Current.SpeckleUnits);
          _levelCache.Add(element.LevelId, (levelName, levelProperties));
        }
        catch (Exception e) when (!e.IsFatal()) { } // TODO: CNX-1376
      }
    }

    // Step 1: create path components. Currently, this is
    // level > category > type
    path.Add(levelName);
    path.Add(element.Category?.Name ?? "No category");
    var typeId = element.GetTypeId();
    if (typeId != ElementId.InvalidElementId)
    {
      var type = doc.GetElement(typeId);
      if (type != null)
      {
        path.Add(type.Name);
      }
    }
    else
    {
      path.Add("No type");
    }

    string fullPathName = string.Join(":", path);
    if (_collectionCache.TryGetValue(fullPathName, out Collection? value))
    {
      return value;
    }

    string flatPathName = "";
    Collection previousCollection = rootObject;

    for (int i = 0; i < path.Count; i++)
    {
      var pathItem = path[i];
      flatPathName = string.IsNullOrEmpty(flatPathName) ? pathItem : flatPathName + ":" + pathItem;
      Collection childCollection;
      if (_collectionCache.TryGetValue(flatPathName, out Collection? collection))
      {
        childCollection = collection;
      }
      else
      {
        childCollection = new Collection(pathItem);
        // add props if it's the 2nd path item (index 1), representing level
        if (i == 1 && levelProperties.Count > 0)
        {
          childCollection["properties"] = levelProperties;
        }

        previousCollection.elements.Add(childCollection);
        _collectionCache[flatPathName] = childCollection;
      }

      previousCollection = childCollection;
    }

    return previousCollection;
  }
}
