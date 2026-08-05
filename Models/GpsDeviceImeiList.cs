using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TrackingMVC.Models
{
    /// <summary>
    /// Maps to [Sunmoon_Enterprises].[sa_lio].[gps_Device_Imei_List]
    /// </summary>
    [Table("gps_Device_Imei_List", Schema = "sa_lio")]
    public class GpsDeviceImeiList
    {
        [Key]
        [Column("pk")]
        public int Pk { get; set; }

        [Column("device_Imei")]
        [MaxLength(20)]
        public string DeviceImei { get; set; } = string.Empty;

        /// <summary>0 = Available, 1 = Already Assigned</summary>
        [Column("AssignFlag")]
        public int AssignFlag { get; set; }
    }
}