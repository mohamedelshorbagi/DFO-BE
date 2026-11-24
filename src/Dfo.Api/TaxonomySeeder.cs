using System.Text.RegularExpressions;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.EntityFrameworkCore;

/// <summary>Default reference data, applied once when the table is empty. After that admins own it (nothing here overwrites their edits).</summary>
public static class TaxonomySeeder
{
    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static async Task SeedIfEmpty(DfoDbContext db)
    {
        var have = (await db.Taxonomies.Select(t => t.Kind).Distinct().ToListAsync()).ToHashSet();
        var items = new List<TaxonomyItem>();
        void Type(string key, string cat, string en, string ar, string icon) =>
            items.Add(new TaxonomyItem { Kind = "propertyType", Key = key, Category = cat, En = en, Ar = ar, Icon = icon });
        Type("villa", "residential", "Standalone Villa", "فيلا مستقلة", "🏡");
        Type("apartment", "residential", "Apartment", "شقة سكنية", "🏢");
        Type("penthouse", "residential", "Penthouse", "بنتهاوس فاخر", "🏙️");
        Type("townhouse", "residential", "Townhouse", "تاون هاوس", "🏘️");
        Type("chalet", "residential", "Coastal Chalet", "شاليه ساحلي", "🏖️");
        Type("duplex", "residential", "Duplex", "دوبلكس", "🏛️");
        Type("twin_house", "residential", "Twin House", "توين هاوس", "🏠");
        Type("office", "commercial", "Administrative Office", "مكتب إداري", "💼");
        Type("retail", "commercial", "Retail Shop / Showroom", "محل / معرض تجاري", "🏪");
        Type("clinic", "commercial", "Medical Clinic", "عيادة طبية", "🏥");
        Type("warehouse", "commercial", "Warehouse / Logistics", "مخزن / لوجستي", "🏭");
        Type("building", "commercial", "Commercial Building", "مبنى تجاري", "🏬");
        Type("land", "commercial", "Commercial Land", "أرض تجارية", "🗺️");

        void Fin(string en, string ar) => items.Add(new TaxonomyItem { Kind = "finishing", Key = en, En = en, Ar = ar });
        Fin("Ultra Super Lux", "ألترا سوبر لوكس");
        Fin("Fully Furnished", "مفروش بالكامل");
        Fin("Semi-Finished", "نصف تشطيب");
        Fin("Core & Shell", "طوب أحمر ومحارة");

        void Amenity(string key, string en, string ar, string cat) =>
            items.Add(new TaxonomyItem { Kind = "amenity", Key = key, En = en, Ar = ar, Category = cat });
        Amenity("pool", "Private Swimming Pool", "حمام سباحة خاص", "Luxury");
        Amenity("security", "24/7 Gated Security", "أمن وحراسة ٢٤/٧", "Security");
        Amenity("garden", "Private Landscaped Garden", "حديقة خاصة", "Outdoors");
        Amenity("garage", "Covered Private Garage", "جراج مغطى", "Convenience");
        Amenity("smart_home", "Smart Home Automation", "نظام سمارت هوم", "Tech");
        Amenity("maid_room", "Maid Room with Bathroom", "غرفة مربية بحمام", "Layout");
        Amenity("solar", "Solar Energy System", "طاقة شمسية", "Green");

        void City(string en, string ar, params string[] compounds)
        {
            var key = Slug(en);
            items.Add(new TaxonomyItem { Kind = "city", Key = key, En = en, Ar = ar });
            foreach (var c in compounds)
                items.Add(new TaxonomyItem { Kind = "compound", Key = $"{key}-{Slug(c)}", En = c, Ar = c, ParentKey = key });
        }
        City("New Cairo", "القاهرة الجديدة", "Palm Hills New Cairo", "Mivida", "Villette SODIC", "Mountain View iCity", "Hyde Park");
        City("Sheikh Zayed", "الشيخ زايد", "Allegria SODIC", "Beverly Hills", "Karmell", "Cairo Gate", "Etapa");
        City("North Coast", "الساحل الشمالي", "Marassi", "Hacienda Bay", "Hacienda Red", "Silversands", "Almaza Bay");
        City("6th of October", "٦ أكتوبر", "Palm Hills October", "O West Orascom", "Sun Capital", "Badya");
        City("El Gouna", "الجونة", "G-Cribs", "Fanadir Bay", "Ancient Sands", "Tawila");
        City("Maadi", "المعادي", "Degla Maadi", "Sarayet El Maadi", "Maadi Heights");
        City("New Capital", "العاصمة الإدارية", "IL Bosco", "Vinci", "Al Maqsad", "Celia");

        // City rows and their compounds travel together; every other kind stands alone.
        items.RemoveAll(i => have.Contains(i.Kind == "compound" ? "city" : i.Kind));
        if (items.Count == 0) return;
        db.Taxonomies.AddRange(items);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { /* another instance seeded at the same moment (unique index on Kind+Key): nothing to do */ }
    }
}
